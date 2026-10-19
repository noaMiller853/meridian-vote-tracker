using Meridian.Domain.Ingestion;
using Meridian.Domain.Membership;
using Meridian.Infrastructure.Files;
using Meridian.Infrastructure.Persistence.Entities;
using Meridian.Infrastructure.Startup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Meridian.Infrastructure.Imports;

/// <summary>
/// Step 4b body of an import, inside the worker's single work transaction (DECISIONS §7):
/// 1. Reading: stream the file line by line into a temp staging table with binary COPY.
/// 2. Classifying: read staging back ordered by vote id through a server-side cursor, so the rows of a vote arrive
///    together however the file is sorted (ING-03); per batch of complete votes, load the approved data with one batch
///    query, classify with the Domain rules and write the results.
/// 3. Writing: counters and the report on the import row. The worker then marks the job Succeeded and commits.
/// The background_jobs row is never touched here (progress uses its own connection).
/// </summary>
public sealed class VoteImportProcessor(IFileStore files, IOptions<ImportOptions> options, ILogger<VoteImportProcessor> logger)
    : IImportProcessor
{
    internal const int FetchSize = 20_000;

    public async Task<ImportProcessResult> ProcessAsync(ImportJob job, NpgsqlConnection connection, NpgsqlTransaction transaction,
        IJobProgress progress, CancellationToken ct)
    {
        var run = new ImportRun(job, connection, transaction, options.Value.CommandTimeoutSeconds);
        await run.LoadReferenceDataAsync(ct);

        var total = await StageAsync(run, progress, ct);
        logger.LogInformation("Import {ImportId}: {Lines} lines staged.", job.ImportId, total);

        await ClassifyAsync(run, total, progress, ct);

        await progress.ReportAsync(BackgroundJob.PhaseWriting, total, total, force: true, ct);
        await run.WriteSummaryAsync(total, ct);
        logger.LogInformation("Import {ImportId}: {Counts}", job.ImportId, run.CountsText());
        return new ImportProcessResult(total);
    }

    private async Task<int> StageAsync(ImportRun run, IJobProgress progress, CancellationToken ct)
    {
        await run.ExecuteAsync("""
            CREATE TEMP TABLE staging_rows (
                row_number int, field_count int, problem_code text, problem_message text, raw_line text,
                assembly_vote_id text, session text, vote_date text, title text,
                country_code text, country_name text, ballot text, published_at text
            ) ON COMMIT DROP
            """, ct);

        await using var stream = await files.OpenReadAsync(run.Job.StorageUri, ct);
        await using var copy = await run.Connection.BeginBinaryImportAsync("""
            COPY staging_rows (row_number, field_count, problem_code, problem_message, raw_line, assembly_vote_id, session,
                               vote_date, title, country_code, country_name, ballot, published_at) FROM STDIN (FORMAT BINARY)
            """, ct);
        copy.Timeout = run.Timeout;

        var lines = 0;
        await progress.ReportAsync(BackgroundJob.PhaseReading, 0, null, force: true, ct);
        await foreach (var line in ExportFileReader.ReadAsync(stream, ct))
        {
            var r = line.Row;
            await copy.StartRowAsync(ct);
            await copy.WriteAsync(r.LineNumber, NpgsqlDbType.Integer, ct);
            await copy.WriteAsync(r.FieldCount, NpgsqlDbType.Integer, ct);
            await Copy.Text(copy, r.LineProblem?.Code.ToString(), ct);
            await Copy.Text(copy, r.LineProblem?.Message, ct);
            await Copy.Text(copy, line.RawText, ct);
            await Copy.Text(copy, r.AssemblyVoteId, ct);
            await Copy.Text(copy, r.Session, ct);
            await Copy.Text(copy, r.VoteDate, ct);
            await Copy.Text(copy, r.Title, ct);
            await Copy.Text(copy, r.CountryCode, ct);
            await Copy.Text(copy, r.CountryName, ct);
            await Copy.Text(copy, r.Ballot, ct);
            await Copy.Text(copy, r.PublishedAt, ct);
            lines++;
            await progress.ReportAsync(BackgroundJob.PhaseReading, lines, null, ct: ct);
        }
        await copy.CompleteAsync(ct);
        return lines;
    }

    private static async Task ClassifyAsync(ImportRun run, int total, IJobProgress progress, CancellationToken ct)
    {
        await run.ExecuteAsync("""
            DECLARE rows_cur NO SCROLL CURSOR FOR
                SELECT row_number, field_count, problem_code, problem_message, raw_line, assembly_vote_id, session, vote_date,
                       title, country_code, country_name, ballot, published_at
                FROM staging_rows
                ORDER BY assembly_vote_id NULLS LAST, row_number
            """, ct);

        await progress.ReportAsync(BackgroundJob.PhaseClassifying, 0, total, force: true, ct);
        var carried = new List<StagedLine>();
        var done = 0;
        while (true)
        {
            var fetched = await FetchAsync(run, ct);
            var end = fetched.Count < FetchSize;
            carried.AddRange(fetched);

            // The last vote of a fetch may continue in the next one, unless this was the last fetch.
            var groups = carried.GroupBy(l => l.Row.AssemblyVoteId).ToList();
            var complete = end ? groups : groups.Take(groups.Count - 1).ToList();
            carried = end ? [] : groups[^1].ToList();

            if (complete.Count > 0)
            {
                await run.ProcessBatchAsync(complete, ct);
                done += complete.Sum(g => g.Count());
                await progress.ReportAsync(BackgroundJob.PhaseClassifying, done, total, ct: ct);
            }
            if (end)
                break;
        }
        await run.ExecuteAsync("CLOSE rows_cur", ct);
    }

    private static async Task<List<StagedLine>> FetchAsync(ImportRun run, CancellationToken ct)
    {
        var lines = new List<StagedLine>(FetchSize);
        await using var cmd = run.Command($"FETCH {FetchSize} FROM rows_cur");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            var raw = reader.GetString(4);
            var problem = Text(2) is { } code
                ? new RowProblem(Enum.Parse<RowProblemCode>(code), "line", Text(3)!, raw)
                : null;
            var row = new RawRow(reader.GetInt32(0), reader.GetInt32(1), Text(5), Text(6), Text(7), Text(8), Text(9), Text(10),
                Text(11), Text(12), problem);
            lines.Add(new StagedLine(row, raw));
        }
        return lines;
    }
}

internal sealed record StagedLine(RawRow Row, string RawText);

internal static class Copy
{
    public static Task Text(NpgsqlBinaryImporter copy, string? value, CancellationToken ct) =>
        value is null ? copy.WriteNullAsync(ct) : copy.WriteAsync(value, NpgsqlDbType.Text, ct);

    public static Task Json(NpgsqlBinaryImporter copy, string? value, CancellationToken ct) =>
        value is null ? copy.WriteNullAsync(ct) : copy.WriteAsync(value, NpgsqlDbType.Jsonb, ct);

    public static Task Long(NpgsqlBinaryImporter copy, long? value, CancellationToken ct) =>
        value is null ? copy.WriteNullAsync(ct) : copy.WriteAsync(value.Value, NpgsqlDbType.Bigint, ct);
}
