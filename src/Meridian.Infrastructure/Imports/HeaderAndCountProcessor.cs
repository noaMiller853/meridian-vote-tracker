using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Meridian.Domain.Ingestion;
using Meridian.Infrastructure.Files;
using Meridian.Infrastructure.Persistence.Entities;
using Npgsql;

namespace Meridian.Infrastructure.Imports;

/// <summary>
/// Step 4a body: checks the header (file-level rule) and streams the file once to count its rows, with live progress.
/// Step 4b replaces it with staging, classification and writing. Never loads the file into memory.
/// </summary>
public sealed class HeaderAndCountProcessor(IFileStore files) : IImportProcessor
{
    internal static readonly CsvConfiguration CsvConfig = new(CultureInfo.InvariantCulture)
    {
        // Row problems are judged by the Domain rules, never by the reader.
        BadDataFound = null,
        MissingFieldFound = null,
        DetectColumnCountChanges = false,
    };

    public async Task<ImportProcessResult> ProcessAsync(ImportJob job, NpgsqlConnection connection, NpgsqlTransaction transaction,
        IJobProgress progress, CancellationToken ct)
    {
        await using var stream = await files.OpenReadAsync(job.StorageUri, ct);
        using var reader = new StreamReader(stream); // detects and skips a BOM
        using var csv = new CsvReader(reader, CsvConfig);

        if (!await csv.ReadAsync() || !csv.ReadHeader() || csv.HeaderRecord is not { Length: > 0 } header)
            throw new ImportFileException("The file is empty: there is no header line. No row was processed.");

        var check = ExportColumns.CheckHeader(header);
        if (!check.IsValid)
            throw new ImportFileException(check.Message, new { check.Missing, check.Unknown, check.Repeated, header });

        var rows = 0;
        await progress.ReportAsync(BackgroundJob.PhaseReading, 0, null, force: true, ct);
        while (await csv.ReadAsync())
        {
            rows++;
            await progress.ReportAsync(BackgroundJob.PhaseReading, rows, null, ct: ct);
        }

        await progress.ReportAsync(BackgroundJob.PhaseReading, rows, rows, force: true, ct);
        return new ImportProcessResult(rows);
    }
}
