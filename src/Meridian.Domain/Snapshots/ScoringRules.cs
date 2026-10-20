using System.Globalization;

namespace Meridian.Domain.Snapshots;

/// <summary>What one vote did to one country's score (DECISIONS §6). Stored as one character per vote.</summary>
public enum VoteOutcome
{
    /// <summary>Y or N, equal to the institute's position: the weight counts and matches.</summary>
    Matched,

    /// <summary>Y or N, opposite to the position: the weight counts, does not match.</summary>
    NotMatched,

    /// <summary>A: not counted.</summary>
    Abstained,

    /// <summary>X: not counted.</summary>
    Absent,

    /// <summary>Not a member on the vote date: not counted.</summary>
    NotAMember,

    /// <summary>A member, but the approved data has no ballot for it: not counted.</summary>
    NoBallot,
}

/// <summary>A vote as frozen in the snapshot: its place in the strings, the institute's position and weight.</summary>
/// <param name="DesiredOutcome">Y (For) or N (Against).</param>
public sealed record FrozenVote(int Ordinal, char DesiredOutcome, int Weight);

/// <param name="Score">0 to 100, 6 decimals; null when no vote counted ("n/a").</param>
public sealed record CountryScore(
    string Outcomes,
    int CountedWeight,
    int MatchedWeight,
    decimal? Score,
    IReadOnlyDictionary<VoteOutcome, int> Counts);

public sealed record RankInput(string CountryCode, string CountryName, int CountedWeight, int MatchedWeight);

/// <param name="Rank">1..M by position; null for a country without a score.</param>
/// <param name="Of">M: the number of countries with a score.</param>
public sealed record RankedCountry(string CountryCode, int? Rank, int Of);

/// <summary>
/// The Alignment Index (spec 7.6) with the rules left open on purpose decided in DECISIONS §6. Pure.
/// Frozen ballot characters, one per included vote in ordinal order: Y, N, A, X, <see cref="NoBallot"/> (a member
/// without a recorded ballot) or <see cref="NotAMember"/> (not a member on the vote date).
/// </summary>
public static class ScoringRules
{
    public const string Version = "1";
    public const char NoBallot = '-';
    public const char NotAMember = '.';

    /// <summary>One character per outcome, in the outcomes string.</summary>
    public static char Code(VoteOutcome outcome) => outcome switch
    {
        VoteOutcome.Matched => 'M',
        VoteOutcome.NotMatched => 'D',
        VoteOutcome.Abstained => 'A',
        VoteOutcome.Absent => 'X',
        VoteOutcome.NotAMember => NotAMember,
        VoteOutcome.NoBallot => NoBallot,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    public static VoteOutcome FromCode(char code) => code switch
    {
        'M' => VoteOutcome.Matched,
        'D' => VoteOutcome.NotMatched,
        'A' => VoteOutcome.Abstained,
        'X' => VoteOutcome.Absent,
        NotAMember => VoteOutcome.NotAMember,
        NoBallot => VoteOutcome.NoBallot,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown outcome code."),
    };

    public static VoteOutcome Outcome(char ballot, char desiredOutcome) => ballot switch
    {
        'Y' or 'N' => ballot == desiredOutcome ? VoteOutcome.Matched : VoteOutcome.NotMatched,
        'A' => VoteOutcome.Abstained,
        'X' => VoteOutcome.Absent,
        NotAMember => VoteOutcome.NotAMember,
        NoBallot => VoteOutcome.NoBallot,
        _ => throw new ArgumentOutOfRangeException(nameof(ballot), ballot, "Unknown ballot character."),
    };

    public static bool Counts(VoteOutcome outcome) => outcome is VoteOutcome.Matched or VoteOutcome.NotMatched;

    /// <summary>A country is ranked only when it was a member on the date of at least one included vote.</summary>
    public static bool IsListed(string ballots) => ballots.Any(c => c != NotAMember);

    /// <summary>score = 100 × matched weight ÷ counted weight; only Y and N count.</summary>
    /// <param name="ballots">One character per vote, in the order of <paramref name="votes"/>.</param>
    public static CountryScore Score(string ballots, IReadOnlyList<FrozenVote> votes)
    {
        if (ballots.Length != votes.Count)
            throw new ArgumentException($"{ballots.Length} ballots for {votes.Count} votes.", nameof(ballots));

        var outcomes = new char[votes.Count];
        var counts = Enum.GetValues<VoteOutcome>().ToDictionary(o => o, _ => 0);
        int counted = 0, matched = 0;
        for (var i = 0; i < votes.Count; i++)
        {
            if (votes[i].Ordinal != i)
                throw new ArgumentException($"Vote at position {i} has ordinal {votes[i].Ordinal}.", nameof(votes));
            var outcome = Outcome(ballots[i], votes[i].DesiredOutcome);
            outcomes[i] = Code(outcome);
            counts[outcome]++;
            if (Counts(outcome))
                counted += votes[i].Weight;
            if (outcome == VoteOutcome.Matched)
                matched += votes[i].Weight;
        }

        decimal? score = counted == 0 ? null : Math.Round(100m * matched / counted, 6, MidpointRounding.AwayFromZero);
        return new CountryScore(new string(outcomes), counted, matched, score, counts);
    }

    /// <summary>
    /// DECISIONS §6: by exact score, highest first (compared as fractions, not rounded values); equal scores by country
    /// name; positional ranks shown as "N of M", M = countries with a score. No score, no rank.
    /// </summary>
    public static IReadOnlyList<RankedCountry> Rank(IEnumerable<RankInput> countries)
    {
        var all = countries.ToList();
        var scored = all.Where(c => c.CountedWeight > 0).ToList();
        scored.Sort((a, b) =>
        {
            // a.matched / a.counted vs b.matched / b.counted, without rounding.
            var byScore = ((long)b.MatchedWeight * a.CountedWeight).CompareTo((long)a.MatchedWeight * b.CountedWeight);
            return byScore != 0 ? byScore : string.Compare(a.CountryName, b.CountryName, CultureInfo.InvariantCulture, CompareOptions.None);
        });

        var ranks = scored.Select((c, i) => new RankedCountry(c.CountryCode, i + 1, scored.Count)).ToList();
        ranks.AddRange(all.Where(c => c.CountedWeight == 0)
            .OrderBy(c => c.CountryName, StringComparer.InvariantCulture)
            .Select(c => new RankedCountry(c.CountryCode, null, scored.Count)));
        return ranks;
    }

    public static string RankText(int? rank, int of) => rank is null ? "n/a" : $"{rank} of {of}";

    /// <summary>The score as shown: one decimal, "n/a" without one (spec example: 66.7).</summary>
    public static string ScoreText(decimal? score) =>
        score is null ? "n/a" : Math.Round(score.Value, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>The rules shown in every snapshot detail (spec 7.6: show the rules chosen for what is open on purpose).</summary>
    public static IReadOnlyList<string> Description { get; } =
    [
        "Score = 100 × (sum of the weights of the votes where the country voted as the institute wanted) ÷ (sum of the weights of the counted votes).",
        "Counted: only a For (Y) or Against (N) ballot. Its weight is added to the denominator, and to the numerator when it equals the institute's position.",
        "Abstention (A): not counted, neither in the numerator nor in the denominator.",
        "Absence (X): not counted, neither in the numerator nor in the denominator.",
        "A vote on a date when the country was not a member: not counted.",
        "A vote where a member country has no approved ballot: not counted.",
        "A country with no counted vote has no score (n/a) and no rank.",
        "Ranking: by exact score, highest first; equal scores in alphabetical order of the country name. Ranks are positions, shown as \"N of M\" where M is the number of countries with a score.",
        "Listed countries: members on the date of at least one included vote.",
        "Votes without a position are never counted; they are listed as excluded only when the creator confirmed it.",
        "The snapshot keeps a frozen copy of the votes, positions, weights and ballots it used; later corrections, conflict decisions and weight changes do not change it.",
    ];
}
