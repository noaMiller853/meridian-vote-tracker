using System.Collections.Concurrent;
using Meridian.Infrastructure.Imports;
using Meridian.Infrastructure.Startup;
using Meridian.Tests.Persistence;
using Meridian.Tests.Startup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Meridian.Tests.Imports;

/// <summary>Worker semantics: progress outside the work transaction, rollback on failure, one job at a time, restarts.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ImportWorkerTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Progress_is_visible_while_the_work_transaction_is_still_open()
    {
        var gate = new GatedProcessor();
        await using var api = await TestApi.StartAsync(fixture, services: s => s.AddScoped<IImportProcessor>(_ => gate));

        var created = await api.UploadAsync("sample.csv", TestApi.SampleCsv);

        // The UI sees the progress written outside the transaction...
        var running = await api.WaitForAsync(created.ImportId, i => i.Done == 5);
        Assert.Equal(("Running", "Classifying", (int?)10), (running.Import.Status, running.Import.Phase, running.Import.Total));
        // ...but not the data written inside it.
        await using (var db = api.Db())
            Assert.False(await db.AuditEvents.AnyAsync(a => a.Entity == "test"));

        gate.Release.SetResult();
        var done = await api.WaitUntilFinishedAsync(created.ImportId);

        Assert.Equal(("Succeeded", 10), (done.Import.Status, done.Import.Done));
        await using (var db = api.Db())
            Assert.True(await db.AuditEvents.AnyAsync(a => a.Entity == "test"));
    }

    [Fact]
    public async Task Failure_rolls_back_everything_the_import_wrote()
    {
        var failing = new GatedProcessor { Fail = true };
        failing.Release.SetResult();
        await using var api = await TestApi.StartAsync(fixture, services: s => s.AddScoped<IImportProcessor>(_ => failing));

        var created = await api.UploadAsync("sample.csv", TestApi.SampleCsv);
        var done = await api.WaitUntilFinishedAsync(created.ImportId);

        Assert.Equal(("Failed", "Processing failed: boom"), (done.Import.Status, done.Import.Error));
        Assert.Equal("boom", done.Report!.Value.GetProperty("error").GetString());
        await using var db = api.Db();
        Assert.False(await db.AuditEvents.AnyAsync(a => a.Entity == "test"));
        Assert.Equal(["Uploaded", "Failed"], await db.AuditEvents.Where(a => a.ImportId == created.ImportId).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync());
    }

    [Fact]
    public async Task Jobs_run_one_at_a_time_even_with_two_api_instances()
    {
        var runs = new ConcurrentBag<(DateTime Start, DateTime End)>();
        var cs = await fixture.CreateEmptyDatabaseAsync();
        await using var first = await TestApi.StartAsync(fixture, cs, s => s.AddScoped<IImportProcessor>(_ => new RecordingProcessor(runs)));
        await using var second = await TestApi.StartAsync(fixture, cs, s => s.AddScoped<IImportProcessor>(_ => new RecordingProcessor(runs)),
            uploadRoot: first.UploadRoot);

        var ids = new List<long>();
        for (var i = 0; i < 4; i++)
            ids.Add((await (i % 2 == 0 ? first : second).UploadAsync($"f{i}.csv", TestApi.SampleCsv)).ImportId);
        foreach (var id in ids)
            Assert.Equal("Succeeded", (await first.WaitUntilFinishedAsync(id)).Import.Status);

        var ordered = runs.OrderBy(r => r.Start).ToList();
        Assert.Equal(4, ordered.Count);
        for (var i = 1; i < ordered.Count; i++)
            Assert.True(ordered[i].Start >= ordered[i - 1].End, $"run {i} started before run {i - 1} ended");
    }

    [Fact]
    public async Task Job_left_running_by_a_restart_is_marked_failed()
    {
        // A database where a job was Running when the previous process stopped.
        var cs = await fixture.CreateEmptyDatabaseAsync();
        await using (var db = PostgresFixture.CreateContext(cs))
            await new DatabaseInitializer(db, NullLogger<DatabaseInitializer>.Instance).InitializeAsync(true, SeedFiles.RealMemberStates);
        long importId;
        await using (var connection = new NpgsqlConnection(cs))
        {
            await connection.OpenAsync();
            await using var cmd = new NpgsqlCommand("""
                WITH job AS (
                    INSERT INTO background_jobs (type, status, phase, done, total, correlation_id, started_at)
                    VALUES ('Import', 'Running', 'Reading', 1200, NULL, 'before-restart', now()) RETURNING id)
                INSERT INTO imports (kind, file_name, storage_uri, sha256, uploaded_by_id, background_job_id,
                                     new_count, unchanged_count, duplicate_count, invalid_count, contradiction_count, conflict_count, normalized_count)
                SELECT 'Votes', 'cut.csv', 'local://x.csv', repeat('b', 64), (SELECT id FROM users WHERE name = 'Dana'), job.id, 0,0,0,0,0,0,0 FROM job
                RETURNING id
                """, connection);
            importId = (long)(await cmd.ExecuteScalarAsync())!;
        }

        await using var api = await TestApi.StartAsync(fixture, cs);
        var done = await api.WaitUntilFinishedAsync(importId);

        Assert.Equal(("Failed", "Interrupted by a restart before it finished. Nothing was stored; upload the file again."),
            (done.Import.Status, done.Import.Error));
        await using var check = api.Db();
        var audit = await check.AuditEvents.Where(a => a.ImportId == importId).Select(a => new { a.Action, Actor = a.Actor.Name, a.CorrelationId }).SingleAsync();
        Assert.Equal(new { Action = "Failed", Actor = "system", CorrelationId = (string?)"before-restart" }, audit);
    }

    [SkippableFact]
    public async Task Historic_file_streams_through_with_progress()
    {
        var path = DataFiles.Find("assembly_historic.csv");
        Skip.If(path is null, "assembly_historic.csv not found (set MERIDIAN_DATA_DIR or put it in data/).");
        await using var api = await TestApi.StartAsync(fixture);

        var created = await api.UploadPathAsync(path!);
        var reading = await api.WaitForAsync(created.ImportId, i => i.Status != "Queued", TimeSpan.FromSeconds(60));
        var done = await api.WaitUntilFinishedAsync(created.ImportId, TimeSpan.FromMinutes(3));

        Assert.NotEqual("Failed", reading.Import.Status);
        Assert.Equal(("Succeeded", 962_171, (int?)962_171), (done.Import.Status, done.Import.Done, done.Import.Total));
    }

    /// <summary>Writes inside the work transaction and reports progress, then waits for the test to let it finish.</summary>
    private sealed class GatedProcessor : IImportProcessor
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail { get; init; }

        public async Task<ImportProcessResult> ProcessAsync(ImportJob job, NpgsqlConnection connection, NpgsqlTransaction transaction,
            IJobProgress progress, CancellationToken ct)
        {
            await using (var cmd = new NpgsqlCommand("""
                INSERT INTO audit_events (entity, entity_id, action, actor_id, import_id, occurred_at)
                VALUES ('test', 'x', 'Written', $1, $2, now())
                """, connection, transaction))
            {
                cmd.Parameters.Add(new() { Value = job.UploadedById });
                cmd.Parameters.Add(new() { Value = job.ImportId });
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await progress.ReportAsync("Classifying", 5, 10, force: true, ct);
            await Release.Task.WaitAsync(ct);
            if (Fail)
                throw new InvalidOperationException("boom");
            return new ImportProcessResult(10);
        }
    }

    private sealed class RecordingProcessor(ConcurrentBag<(DateTime, DateTime)> runs) : IImportProcessor
    {
        public async Task<ImportProcessResult> ProcessAsync(ImportJob job, NpgsqlConnection connection, NpgsqlTransaction transaction,
            IJobProgress progress, CancellationToken ct)
        {
            var start = DateTime.UtcNow;
            await Task.Delay(300, ct);
            runs.Add((start, DateTime.UtcNow));
            return new ImportProcessResult(3);
        }
    }
}

/// <summary>The large data files are not in Git; tests that need them look in MERIDIAN_DATA_DIR, data/ and the parent folder.</summary>
internal static class DataFiles
{
    public static string? Find(string fileName)
    {
        var root = Path.GetDirectoryName(SeedFiles.RealMemberStates)!; // <repo>/seed
        var repo = Path.GetDirectoryName(root)!;
        string?[] candidates =
        [
            Environment.GetEnvironmentVariable("MERIDIAN_DATA_DIR"),
            Path.Combine(repo, "data"),
            Path.GetDirectoryName(repo),
        ];
        return candidates.Where(d => d is not null).Select(d => Path.Combine(d!, fileName)).FirstOrDefault(File.Exists);
    }
}
