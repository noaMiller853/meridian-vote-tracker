using System.Text.Json;
using Npgsql;

namespace Meridian.Infrastructure.Imports;

/// <summary>Section "Imports".</summary>
public sealed class ImportOptions
{
    /// <summary>How often the worker looks for queued jobs when it was not signalled.</summary>
    public int PollSeconds { get; set; } = 5;

    /// <summary>Minimum time between two progress updates within one phase.</summary>
    public int ProgressIntervalMs { get; set; } = 1000;

    /// <summary>Command timeout for the long import transaction (the historic file has about a million rows).</summary>
    public int CommandTimeoutSeconds { get; set; } = 900;
}

/// <summary>A claimed import job, as the worker passes it to the processor.</summary>
public sealed record ImportJob(
    long JobId,
    long ImportId,
    string FileName,
    string StorageUri,
    string Sha256,
    long UploadedById,
    string? CorrelationId);

public sealed record ImportProcessResult(int Rows);

/// <summary>
/// The body of an import. It runs inside the worker's single work transaction; the worker marks the job Succeeded in the
/// same transaction and commits, or rolls back and marks it Failed. Progress goes through <see cref="IJobProgress"/>,
/// which writes outside that transaction so the UI sees it while the import runs (ING-02).
/// </summary>
public interface IImportProcessor
{
    Task<ImportProcessResult> ProcessAsync(ImportJob job, NpgsqlConnection connection, NpgsqlTransaction transaction,
        IJobProgress progress, CancellationToken ct);
}

public interface IJobProgress
{
    /// <summary>Records progress. Throttled within a phase; a phase change or <paramref name="force"/> is always written.</summary>
    ValueTask ReportAsync(string phase, int done, int? total, bool force = false, CancellationToken ct = default);
}

/// <summary>A problem with the file as a whole (ING-07: the only kind of problem that fails a file).</summary>
public sealed class ImportFileException(string message, object? details = null) : Exception(message)
{
    public string ReportJson { get; } = JsonSerializer.Serialize(new { status = "Failed", fileError = message, details });
}

/// <summary>Wakes the worker right after an upload instead of waiting for the next poll.</summary>
public sealed class ImportSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct) => await _signal.WaitAsync(timeout, ct);
}
