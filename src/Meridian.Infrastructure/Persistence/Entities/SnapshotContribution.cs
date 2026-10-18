namespace Meridian.Infrastructure.Persistence.Entities;

public class SnapshotContribution
{
    public long SnapshotId { get; set; }
    public string CountryCode { get; set; } = null!;
    public long VoteId { get; set; }

    /// <summary>Y, N, A or X. Null when the country has no ballot on this vote.</summary>
    public string? Ballot { get; set; }

    public bool Counted { get; set; }
    public string? Reason { get; set; }
    public short Weight { get; set; }
    public bool Matched { get; set; }

    public Snapshot Snapshot { get; set; } = null!;
    public Country Country { get; set; } = null!;
    public Vote Vote { get; set; } = null!;
}
