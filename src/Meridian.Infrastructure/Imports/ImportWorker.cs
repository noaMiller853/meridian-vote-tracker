using System.Text.Json;
using Meridian.Infrastructure.Persistence.Entities;
using Meridian.Infrastructure.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Meridian.Infrastructure.Imports;

/// <summary>
/// Processes queued imports one at a time (DECISIONS §7). A session-level advisory lock makes it one worker across all
/// API instances. Each job runs in one work transaction; progress is written outside it.
/// </summary>
public sealed class ImportWorker(
    NpgsqlDataSource dataSource,
    IServiceScopeFactory scopes,
    ImportSignal signal,
    IOptions<ImportOptions> options,
    ILogger<ImportWorker> logger) : BackgroundService
{
    private const long WorkerLockKey = 7_302_114_002;

    internal const string InterruptedMessage =
        "Interrupted by a restart before it finished. Nothing was stored; upload the file again.";

    private ImportOptions Options => options.Value;

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
                logger.LogError(ex, "The import worker loop failed; it retries at the next poll.");
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
        // Held on its own connection for as long as this instance works; closing the connection releases it too.
        await using var lockConnection = await dataSource.OpenConnectionAsync(ct);
        await using (var tryLock = new NpgsqlCommand($"SELECT pg_try_advisory_lock({WorkerLockKey})", lockConnection))
            if (!(bool)(await tryLock.ExecuteScalarAsync(ct))!)
                return; // another instance is the worker

        try
        {
            var systemUserId = await SystemUserIdAsync(ct);

            // With the lock held, a job still marked Running was cut off by a crash or restart. Its work transaction
            // rolled back, so nothing half-written exists.
            await FailJobsAsync("TRUE", InterruptedMessage, null, systemUserId, null, ct);

            while (await ClaimNextAsync(ct) is { } job)
                await RunAsync(job, systemUserId, ct);
        }
        finally
        {
            await using var unlock = new NpgsqlCommand($"SELECT pg_advisory_unlock({WorkerLockKey})", lockConnection);
            await unlock.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    private async Task<ImportJob?> ClaimNextAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            WITH next AS (
                SELECT id FROM background_jobs
                WHERE type = 'Import' AND status = 'Queued'
                ORDER BY id
                FOR UPDATE SKIP LOCKED
                LIMIT 1)
            UPDATE background_jobs j
            SET status = 'Running', phase = 'Reading', done = 0, total = NULL, started_at = now()
            FROM next
            WHERE j.id = next.id
            RETURNING j.id, j.correlation_id
            """);
        long jobId;
        string? correlationId;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                return null;
            jobId = reader.GetInt64(0);
            correlationId = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        await using var importCmd = dataSource.CreateCommand(
            "SELECT id, file_name, storage_uri, sha256, uploaded_by_id FROM imports WHERE background_job_id = $1");
        importCmd.Parameters.Add(new() { Value = jobId });
        await using var importReader = await importCmd.ExecuteReaderAsync(ct);
        if (!await importReader.ReadAsync(ct))
            throw new InvalidOperationException($"Background job {jobId} has no import.");

        return new ImportJob(jobId, importReader.GetInt64(0), importReader.GetString(1), importReader.GetString(2),
            importReader.GetString(3), importReader.GetInt64(4), correlationId);
    }

    private async Task RunAsync(ImportJob job, long systemUserId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = job.CorrelationId,
            ["ImportId"] = job.ImportId,
            ["JobId"] = job.JobId,
        });
        logger.LogInformation("Import {ImportId} started: {FileName}.", job.ImportId, job.FileName);

        var processor = scope.ServiceProvider.GetRequiredService<IImportProcessor>();
        var progress = new JobProgress(dataSource, job.JobId, TimeSpan.FromMilliseconds(Options.ProgressIntervalMs));

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        try
        {
            var result = await processor.ProcessAsync(job, connection, tx, progress, ct);

            await using (var done = new NpgsqlCommand("""
                UPDATE background_jobs
                SET status = 'Succeeded', phase = NULL, done = $2, total = $2, finished_at = now()
                WHERE id = $1
                """, connection, tx) { CommandTimeout = Options.CommandTimeoutSeconds })
            {
                done.Parameters.Add(new() { Value = job.JobId });
                done.Parameters.Add(new() { Value = result.Rows });
                await done.ExecuteNonQueryAsync(ct);
            }
            await InsertAuditAsync(connection, tx, job, "Processed", JsonSerializer.Serialize(new { rows = result.Rows }), systemUserId, ct);
            await tx.CommitAsync(ct);
            logger.LogInformation("Import {ImportId} succeeded: {Rows} rows.", job.ImportId, result.Rows);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown: the transaction rolls back, and the next start marks the job as interrupted.
            throw;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            string message, report;
            if (ex is ImportFileException file)
            {
                // Written for the user: which columns are wrong.
                (message, report) = (file.Message, file.ReportJson);
                logger.LogWarning("Import {ImportId} rejected: {Message}", job.ImportId, message);
            }
            else
            {
                // Never show an exception message (it can contain SQL, paths or data); the log has the details.
                message = $"Processing failed. Reference: {job.CorrelationId ?? $"job {job.JobId}"}; details are in the server log.";
                report = JsonSerializer.Serialize(new { status = "Failed", error = message });
                logger.LogError(ex, "Import {ImportId} failed.", job.ImportId);
            }

            await FailJobsAsync("id = $4", message, report, systemUserId, job.JobId, CancellationToken.None);
        }
    }

    /// <summary>
    /// Marks matching Running import jobs as Failed, stores the reason in the import report and writes one audit event
    /// per import, all in one statement (autocommit).
    /// </summary>
    private async Task FailJobsAsync(string condition, string message, string? reportJson, long systemUserId, long? jobId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"""
            WITH failed AS (
                UPDATE background_jobs
                SET status = 'Failed', phase = NULL, error = $1, finished_at = now()
                WHERE type = 'Import' AND status = 'Running' AND {condition}
                RETURNING id, correlation_id),
            reported AS (
                UPDATE imports i
                SET report = COALESCE($2::jsonb, jsonb_build_object('status', 'Failed', 'error', $1::text))
                FROM failed f
                WHERE i.background_job_id = f.id
                RETURNING i.id, f.correlation_id)
            INSERT INTO audit_events (entity, entity_id, action, new_value, actor_id, import_id, correlation_id, occurred_at)
            SELECT 'import', r.id::text, 'Failed', jsonb_build_object('error', $1::text), $3, r.id, r.correlation_id, now()
            FROM reported r
            """);
        cmd.Parameters.Add(new() { Value = message });
        cmd.Parameters.Add(new() { Value = (object?)reportJson ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = systemUserId });
        if (jobId is not null)
            cmd.Parameters.Add(new() { Value = jobId.Value });

        var count = await cmd.ExecuteNonQueryAsync(ct);
        if (jobId is null && count > 0)
            logger.LogWarning("Marked {Count} interrupted import job(s) as Failed.", count);
    }

    private async Task InsertAuditAsync(NpgsqlConnection connection, NpgsqlTransaction tx, ImportJob job, string action, string newValue,
        long actorId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO audit_events (entity, entity_id, action, new_value, actor_id, import_id, correlation_id, occurred_at)
            VALUES ('import', $1, $2, $3::jsonb, $4, $5, $6, now())
            """, connection, tx);
        cmd.Parameters.Add(new() { Value = job.ImportId.ToString() });
        cmd.Parameters.Add(new() { Value = action });
        cmd.Parameters.Add(new() { Value = newValue });
        cmd.Parameters.Add(new() { Value = actorId });
        cmd.Parameters.Add(new() { Value = job.ImportId });
        cmd.Parameters.Add(new() { Value = (object?)job.CorrelationId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<long> SystemUserIdAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT id FROM users WHERE name = $1");
        cmd.Parameters.Add(new() { Value = DatabaseInitializer.SystemUserName });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
