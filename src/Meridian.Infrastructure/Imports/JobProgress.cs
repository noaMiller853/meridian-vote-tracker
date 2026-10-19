using System.Diagnostics;
using Npgsql;

namespace Meridian.Infrastructure.Imports;

/// <summary>
/// Writes progress with its own pooled connection in autocommit mode, so it is visible while the import's work
/// transaction is still open, and survives a page refresh (ING-02).
/// </summary>
public sealed class JobProgress(NpgsqlDataSource dataSource, long jobId, TimeSpan interval) : IJobProgress
{
    private string? _phase;
    private long _lastWrite;

    public ValueTask ReportAsync(string phase, int done, int? total, bool force = false, CancellationToken ct = default)
    {
        if (!force && phase == _phase && Stopwatch.GetElapsedTime(_lastWrite) < interval)
            return ValueTask.CompletedTask;
        return new ValueTask(WriteAsync(phase, done, total, ct));
    }

    private async Task WriteAsync(string phase, int done, int? total, CancellationToken ct)
    {
        _phase = phase;
        _lastWrite = Stopwatch.GetTimestamp();
        await using var cmd = dataSource.CreateCommand("UPDATE background_jobs SET phase = $1, done = $2, total = $3 WHERE id = $4 AND status = 'Running'");
        cmd.Parameters.Add(new() { Value = phase });
        cmd.Parameters.Add(new() { Value = done });
        cmd.Parameters.Add(new() { Value = (object?)total ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
        cmd.Parameters.Add(new() { Value = jobId });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
