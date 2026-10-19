using Meridian.Infrastructure.Files;
using Meridian.Infrastructure.Persistence.Entities;
using Npgsql;

namespace Meridian.Infrastructure.Imports;

/// <summary>
/// Step 4a body: reads the header (file-level rule) and streams every physical line once to count the rows, with live
/// progress. Step 4b replaces it with staging, classification and writing. Never loads the file into memory.
/// </summary>
public sealed class HeaderAndCountProcessor(IFileStore files) : IImportProcessor
{
    public async Task<ImportProcessResult> ProcessAsync(ImportJob job, NpgsqlConnection connection, NpgsqlTransaction transaction,
        IJobProgress progress, CancellationToken ct)
    {
        await using var stream = await files.OpenReadAsync(job.StorageUri, ct);

        var rows = 0;
        await progress.ReportAsync(BackgroundJob.PhaseReading, 0, null, force: true, ct);
        await foreach (var _ in ExportFileReader.ReadAsync(stream, ct))
        {
            rows++;
            await progress.ReportAsync(BackgroundJob.PhaseReading, rows, null, ct: ct);
        }

        await progress.ReportAsync(BackgroundJob.PhaseReading, rows, rows, force: true, ct);
        return new ImportProcessResult(rows);
    }
}
