namespace Meridian.Infrastructure.Persistence.Entities;

/// <summary>Frozen copy of a selected vote, its position and weight at snapshot time (SNP-04).</summary>
public class SnapshotVote
{
    public long SnapshotId { get; set; }
    public long VoteId { get; set; }

    /// <summary>
    /// Included votes only: 0..n-1, the place of this vote in every country's ballots and outcomes strings. Null for an
    /// excluded vote.
    /// </summary>
    public int? Ordinal { get; set; }

    // Frozen details, so a later correction of the vote does not change what the snapshot shows.
    public string AssemblyVoteId { get; set; } = null!;
    public int Session { get; set; }
    public DateOnly VoteDate { get; set; }
    public string Title { get; set; } = null!;

    public string? DesiredOutcome { get; set; }
    public short? Weight { get; set; }
    public bool Included { get; set; }
    public string? Reason { get; set; }

    public Snapshot Snapshot { get; set; } = null!;
    public Vote Vote { get; set; } = null!;
}
