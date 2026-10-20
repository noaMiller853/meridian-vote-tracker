using System.Data;
using System.Text.Json;
using Meridian.Infrastructure.Imports;
using Meridian.Infrastructure.Startup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Meridian.Infrastructure.Snapshots;

/// <summary>
/// Calculates requested snapshots one at a time (SNP-02), separately from the import worker so a snapshot does not wait
/// behind a long import. Same pattern as imports (DECISIONS §7): a session advisory lock makes it one worker across API
/// instances, jobs are claimed with SKIP LOCKED, each job is one work transaction, and progress is written outside it.
/// The work transaction is REPEATABLE READ: the snapshot sees the approved data as of one moment, even if a conflict
/// decision or an import commits while it runs (SNP-04).
/// </summary>
public sealed class SnapshotWorker(
    NpgsqlDataSource dataSource,
    SnapshotSignal signal,
    IOptions<SnapshotOptions> options,
    ILogger<SnapshotWorker> logger) : BackgroundService
{
    private const long WorkerLockKey = 7_302_114_003;

    public const string InterruptedMessage = "Interrupted by a restart before it finished. Nothing was stored; request the snapshot again.";

    private SnapshotOptions Options => options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainQueueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The snapshot worker loop failed; it retries at the next poll.");
            }

            try
            {
                await signal.WaitAsync(TimeSpan.FromSeconds(Options.PollSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task DrainQueueAsync(CancellationToken ct)
    {
        await using var lockConnection = await dataSource.OpenConnectionAsync(ct);
        await using (var tryLock = new NpgsqlCommand($"SELECT pg_try_advisory_lock({WorkerLockKey})", lockConnection))
            if (!(bool)(await tryLock.ExecuteScalarAsync(ct))!)
                return;

        try
        {
            var systemUserId = await SystemUserIdAsync(ct);
            // With the lock held, a snapshot job still Running was cut off by a restart. If its snapshot was already
            // saved, only marking the job finished was lost; otherwise its work transaction rolled back.
            await CompleteSavedJobsAsync(ct);
            await FailAsync("TRUE", InterruptedMessage, systemUserId, null, ct);

            while (await ClaimNextAsync(ct) is { } job)
                await RunAsync(job, systemUserId, ct);
        }
        finally
        {
            await using var unlock = new NpgsqlCommand($"SELECT pg_advisory_unlock({WorkerLockKey})", lockConnection);
            await unlock.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    private async Task<SnapshotJob?> ClaimNextAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            WITH next AS (
                SELECT id FROM background_jobs
                WHERE type = 'Snapshot' AND status = 'Queued'
                ORDER BY id
                FOR UPDATE SKIP LOCKED
                LIMIT 1),
            claimed AS (
                UPDATE background_jobs j
                SET status = 'Running', phase = 'Selecting', done = 0, total = NULL, started_at = now()
                FROM next
                WHERE j.id = next.id
                RETURNING j.id, j.correlation_id)
            SELECT c.id, s.id, s.filter::text, s.exclude_without_position, s.created_by_id, c.correlation_id
            FROM claimed c
            JOIN snapshots s ON s.background_job_id = c.id
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new SnapshotJob(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetBoolean(3), reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private async Task RunAsync(SnapshotJob job, long systemUserId, CancellationToken ct)
    {
        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = job.CorrelationId,
            ["SnapshotId"] = job.SnapshotId,
            ["JobId"] = job.JobId,
        });
        logger.LogInformation("Snapshot {SnapshotId} started.", job.SnapshotId);

        var progress = new JobProgress(dataSource, job.JobId, TimeSpan.FromMilliseconds(Options.ProgressIntervalMs));
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        SnapshotResult result;
        await using (var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct))
        {
            try
            {
                result = await new SnapshotCalculator(connection, tx, Options.CommandTimeoutSeconds).RunAsync(job, progress, ct);

                await using (var saved = new NpgsqlCommand("""
                    INSERT INTO audit_events (entity, entity_id, action, new_value, actor_id, correlation_id, occurred_at)
                    VALUES ('snapshot', $1, 'SnapshotSaved', $2::jsonb, $3, $4, clock_timestamp())
                    """, connection, tx))
                {
                    saved.Parameters.Add(new() { Value = job.SnapshotId.ToString() });
                    saved.Parameters.Add(new() { Value = JsonSerializer.Serialize(result, SnapshotCalculator.Json) });
                    saved.Parameters.Add(new() { Value = systemUserId });
                    saved.Parameters.Add(new() { Value = (object?)job.CorrelationId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
                    await saved.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // shutdown: rolled back; the next start marks it as interrupted
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync(CancellationToken.None);
                string message;
                if (ex is SnapshotRejectedException rejected)
                {
                    message = rejected.Message;
                    logger.LogWarning("Snapshot {SnapshotId} not calculated: {Message}", job.SnapshotId, message);
                }
                else
                {
                    // Never show an exception message; the log has the details.
                    message = $"Calculation failed. Reference: {job.CorrelationId ?? $"job {job.JobId}"}; details are in the server log.";
                    logger.LogError(ex, "Snapshot {SnapshotId} failed.", job.SnapshotId);
                }
                await FailAsync("id = $3", message, systemUserId, job.JobId, CancellationToken.None);
                return;
            }
        }

        // Outside the work transaction: the progress writer updates this row while the calculation runs, so a
        // REPEATABLE READ transaction may not update it too (40001). The snapshot is already saved and committed; if
        // this update is lost, the next start completes the job (CompleteSavedJobsAsync).
        await using (var done = dataSource.CreateCommand("""
            UPDATE background_jobs
            SET status = 'Succeeded', phase = NULL, done = $2, total = $2, finished_at = clock_timestamp()
            WHERE id = $1 AND status = 'Running'
            """))
        {
            done.Parameters.Add(new() { Value = job.JobId });
            done.Parameters.Add(new() { Value = result.Countries });
            await done.ExecuteNonQueryAsync(CancellationToken.None);
        }
        logger.LogInformation("Snapshot {SnapshotId} saved: {Included} votes, {Countries} countries, quarter {Quarter}.",
            job.SnapshotId, result.IncludedVotes, result.Countries, result.Quarter);
    }

    /// <summary>Running jobs whose snapshot was already saved: only marking the job finished was lost.</summary>
    private async Task CompleteSavedJobsAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE background_jobs j
            SET status = 'Succeeded', phase = NULL, finished_at = now()
            FROM snapshots s
            WHERE s.background_job_id = j.id AND j.type = 'Snapshot' AND j.status = 'Running' AND s.status IN ('Saved', 'Published')
            """);
        var count = await cmd.ExecuteNonQueryAsync(ct);
        if (count > 0)
            logger.LogWarning("Marked {Count} snapshot job(s) whose snapshot was saved as Succeeded.", count);
    }

    /// <summary>Marks matching Running snapshot jobs and their snapshots as Failed, with one audit event each.</summary>
    private async Task FailAsync(string condition, string message, long systemUserId, long? jobId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"""
            WITH failed AS (
                UPDATE background_jobs
                SET status = 'Failed', phase = NULL, error = $1, finished_at = now()
                WHERE type = 'Snapshot' AND status = 'Running' AND {condition}
                RETURNING id, correlation_id),
            snap AS (
                UPDATE snapshots s
                SET status = 'Failed'
                FROM failed f
                WHERE s.background_job_id = f.id AND s.status = 'Calculating' -- a saved snapshot is never marked Failed
                RETURNING s.id, f.correlation_id)
            INSERT INTO audit_events (entity, entity_id, action, new_value, actor_id, correlation_id, occurred_at)
            SELECT 'snapshot', s.id::text, 'SnapshotFailed', jsonb_build_object('error', $1::text), $2, s.correlation_id, now()
            FROM snap s
            """);
        cmd.Parameters.Add(new() { Value = message });
        cmd.Parameters.Add(new() { Value = systemUserId });
        if (jobId is not null)
            cmd.Parameters.Add(new() { Value = jobId.Value });

        var count = await cmd.ExecuteNonQueryAsync(ct);
        if (jobId is null && count > 0)
            logger.LogWarning("Marked {Count} interrupted snapshot job(s) as Failed.", count);
    }

    private async Task<long> SystemUserIdAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT id FROM users WHERE name = $1");
        cmd.Parameters.Add(new() { Value = DatabaseInitializer.SystemUserName });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
