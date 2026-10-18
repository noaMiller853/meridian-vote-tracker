namespace Meridian.Infrastructure.Persistence.Entities;

/// <summary>Frozen copy of the position and weight of a vote at snapshot time (SNP-04).</summary>
public class SnapshotVote
{
    public long SnapshotId { get; set; }
    public long VoteId { get; set; }
    public string AssemblyVoteId { get; set; } = null!;
    public string? DesiredOutcome { get; set; }
    public short? Weight { get; set; }
    public bool Included { get; set; }
    public string? Reason { get; set; }

    public Snapshot Snapshot { get; set; } = null!;
    public Vote Vote { get; set; } = null!;
}
