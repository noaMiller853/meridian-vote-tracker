namespace Meridian.Infrastructure.Persistence.Entities;

/// <summary>
/// One listed country of a snapshot: its frozen ballots, the outcome of every included vote, and the result (SNP-03).
/// Character i of <see cref="Ballots"/> and <see cref="Outcomes"/> belongs to the snapshot vote with ordinal i.
/// </summary>
public class SnapshotScore
{
    public long SnapshotId { get; set; }
    public string CountryCode { get; set; } = null!;

    /// <summary>Frozen, like the vote details.</summary>
    public string CountryName { get; set; } = null!;

    /// <summary>Y, N, A, X, '-' (a member without a ballot) or '.' (not a member on the vote date).</summary>
    public string Ballots { get; set; } = null!;

    /// <summary>M (matched), D (did not match), A, X, '.' or '-' (Domain ScoringRules).</summary>
    public string Outcomes { get; set; } = null!;

    /// <summary>Null when the country has no counted votes (shown as n/a).</summary>
    public decimal? Score { get; set; }

    /// <summary>Null exactly when Score is null.</summary>
    public int? Rank { get; set; }

    public int CountedWeight { get; set; }
    public int MatchedWeight { get; set; }

    public int MatchedVotes { get; set; }
    public int NotMatchedVotes { get; set; }
    public int AbstainedVotes { get; set; }
    public int AbsentVotes { get; set; }
    public int NotAMemberVotes { get; set; }
    public int NoBallotVotes { get; set; }

    public Snapshot Snapshot { get; set; } = null!;
    public Country Country { get; set; } = null!;
}
