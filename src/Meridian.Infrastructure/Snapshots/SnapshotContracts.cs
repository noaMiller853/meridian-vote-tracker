using Meridian.Infrastructure.Imports;

namespace Meridian.Infrastructure.Snapshots;

/// <summary>Section "Snapshots".</summary>
public sealed class SnapshotOptions
{
    /// <summary>How often the worker looks for queued snapshots when it was not signalled.</summary>
    public int PollSeconds { get; set; } = 5;

    /// <summary>Minimum time between two progress updates within one phase.</summary>
    public int ProgressIntervalMs { get; set; } = 1000;

    /// <summary>Command timeout for the calculation over the whole history (about a million country votes).</summary>
    public int CommandTimeoutSeconds { get; set; } = 900;
}

/// <summary>Wakes the snapshot worker right after a snapshot is requested.</summary>
public sealed class SnapshotSignal : JobSignal;

/// <summary>A claimed snapshot job, as the worker passes it to the calculator.</summary>
public sealed record SnapshotJob(long JobId, long SnapshotId, string SelectionJson, bool ExcludeWithoutPosition, long CreatedById, string? CorrelationId);

/// <summary>The selection as stored in snapshots.filter (jsonb).</summary>
public sealed record StoredSelection(DateOnly? DateFrom, DateOnly? DateTo, int? Session, IReadOnlyList<string> AssemblyVoteIds);

public sealed record SnapshotResult(int IncludedVotes, int ExcludedVotes, int Countries, int RankedCountries, string Quarter);

/// <summary>
/// A reason for the user why a snapshot could not be calculated (shown as the job error), as opposed to an unexpected
/// failure, whose details stay in the server log.
/// </summary>
public sealed class SnapshotRejectedException(string message) : Exception(message);
