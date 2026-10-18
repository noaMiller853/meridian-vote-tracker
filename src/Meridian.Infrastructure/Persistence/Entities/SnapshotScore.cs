namespace Meridian.Infrastructure.Persistence.Entities;

public class SnapshotScore
{
    public long SnapshotId { get; set; }
    public string CountryCode { get; set; } = null!;

    /// <summary>Null when the country has no counted votes (shown as n/a).</summary>
    public decimal? Score { get; set; }

    /// <summary>Null exactly when Score is null.</summary>
    public int? Rank { get; set; }

    public int CountedWeight { get; set; }
    public int MatchedWeight { get; set; }

    public Snapshot Snapshot { get; set; } = null!;
    public Country Country { get; set; } = null!;
}
