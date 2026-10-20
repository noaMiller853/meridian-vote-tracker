using Meridian.Domain.Snapshots;

namespace Meridian.Tests.Domain;

/// <summary>Spec 7.6 formula and the rules decided in DECISIONS §6.</summary>
public class ScoringRulesTests
{
    private static List<FrozenVote> Votes(params (char Desired, int Weight)[] votes) =>
        votes.Select((v, i) => new FrozenVote(i, v.Desired, v.Weight)).ToList();

    [Fact]
    public void Spec_example_is_4_of_6()
    {
        // For in a vote of weight 4 where the institute wanted For, Against in a vote of weight 2 where it wanted For.
        var score = ScoringRules.Score("YN", Votes(('Y', 4), ('Y', 2)));

        Assert.Equal((6, 4, 66.666667m), (score.CountedWeight, score.MatchedWeight, score.Score));
        Assert.Equal("66.7", ScoringRules.ScoreText(score.Score));
        Assert.Equal("MD", score.Outcomes);
    }

    [Fact]
    public void Against_matches_a_position_against()
    {
        var score = ScoringRules.Score("NY", Votes(('N', 3), ('N', 1)));

        Assert.Equal((4, 3, 75m), (score.CountedWeight, score.MatchedWeight, score.Score));
    }

    [Fact]
    public void Abstention_absence_non_member_and_no_ballot_do_not_count()
    {
        var votes = Votes(('Y', 4), ('Y', 5), ('Y', 5), ('N', 5), ('N', 5));

        var score = ScoringRules.Score("YAX.-", votes);

        Assert.Equal((4, 4, 100m), (score.CountedWeight, score.MatchedWeight, score.Score));
        Assert.Equal("MAX.-", score.Outcomes);
        Assert.Equal(1, score.Counts[VoteOutcome.Matched]);
        Assert.Equal(1, score.Counts[VoteOutcome.Abstained]);
        Assert.Equal(1, score.Counts[VoteOutcome.Absent]);
        Assert.Equal(1, score.Counts[VoteOutcome.NotAMember]);
        Assert.Equal(1, score.Counts[VoteOutcome.NoBallot]);
    }

    [Fact]
    public void Country_with_no_counted_vote_has_no_score()
    {
        var score = ScoringRules.Score("AX-", Votes(('Y', 4), ('N', 2), ('Y', 1)));

        Assert.Equal((0, 0, (decimal?)null), (score.CountedWeight, score.MatchedWeight, score.Score));
        Assert.Equal("n/a", ScoringRules.ScoreText(score.Score));
    }

    [Theory]
    [InlineData("YN", 50)]
    [InlineData("NN", 0)]
    [InlineData("YY", 100)]
    public void Score_is_between_0_and_100(string ballots, int expected) =>
        Assert.Equal(expected, ScoringRules.Score(ballots, Votes(('Y', 3), ('Y', 3))).Score);

    [Fact]
    public void Only_countries_that_were_members_of_at_least_one_vote_are_listed()
    {
        Assert.False(ScoringRules.IsListed("..."));
        Assert.True(ScoringRules.IsListed("..-"));   // a member without a ballot is listed (and gets n/a)
        Assert.True(ScoringRules.IsListed(".A."));
    }

    [Fact]
    public void Ballots_and_votes_must_line_up()
    {
        Assert.Throws<ArgumentException>(() => ScoringRules.Score("YN", Votes(('Y', 1))));
        Assert.Throws<ArgumentException>(() => ScoringRules.Score("Y", [new FrozenVote(1, 'Y', 1)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScoringRules.Score("?", Votes(('Y', 1))));
    }

    [Fact]
    public void Outcome_codes_round_trip()
    {
        foreach (var outcome in Enum.GetValues<VoteOutcome>())
            Assert.Equal(outcome, ScoringRules.FromCode(ScoringRules.Code(outcome)));
    }

    // ---- Ranking ----

    [Fact]
    public void Ranks_by_exact_score_then_by_name_and_countries_without_score_have_no_rank()
    {
        var ranked = ScoringRules.Rank(
        [
            new RankInput("GHA", "Ghana", 3, 2),     // 66.67
            new RankInput("BRA", "Brazil", 6, 4),    // 66.67: same exact score, Brazil first by name
            new RankInput("AFG", "Afghanistan", 0, 0),
            new RankInput("FRA", "France", 4, 4),    // 100
            new RankInput("ZMB", "Zambia", 7, 1),
        ]);

        Assert.Equal(["FRA", "BRA", "GHA", "ZMB", "AFG"], ranked.Select(r => r.CountryCode));
        Assert.Equal([1, 2, 3, 4, (int?)null], ranked.Select(r => r.Rank));
        Assert.All(ranked, r => Assert.Equal(4, r.Of));
        Assert.Equal(("2 of 4", "n/a"), (ScoringRules.RankText(2, 4), ScoringRules.RankText(null, 4)));
    }

    [Fact]
    public void Scores_that_round_to_the_same_value_are_still_ordered_exactly()
    {
        // 2/3 and 666667/1000000 both round to 66.666667 but are not equal.
        var ranked = ScoringRules.Rank([new RankInput("AAA", "A", 3, 2), new RankInput("BBB", "B", 1_000_000, 666_667)]);

        Assert.Equal(["BBB", "AAA"], ranked.Select(r => r.CountryCode));
    }

    [Fact]
    public void The_rules_shown_cover_every_case_left_open()
    {
        var text = string.Join(" ", ScoringRules.Description);

        Assert.Contains("Abstention (A): not counted", text);
        Assert.Contains("Absence (X): not counted", text);
        Assert.Contains("not a member: not counted", text);
        Assert.Contains("no counted vote has no score (n/a) and no rank", text);
    }

    // ---- Selection and quarter ----

    [Theory]
    [InlineData(2024, 1, 1, "2024-Q1")]
    [InlineData(2024, 3, 31, "2024-Q1")]
    [InlineData(2024, 4, 1, "2024-Q2")]
    [InlineData(2023, 12, 31, "2023-Q4")]
    public void Quarter_of_a_date(int year, int month, int day, string expected) =>
        Assert.Equal(expected, Quarter.Of(new DateOnly(year, month, day)));

    [Fact]
    public void Selection_needs_a_name_and_at_least_one_criterion()
    {
        var errors = new SnapshotSelection(null, null, null, []).Validate(" ");

        Assert.Equal(["name", "assemblyVoteIds"], errors.Select(e => e.Field));
    }

    [Fact]
    public void Selection_errors_are_under_their_fields()
    {
        var errors = new SnapshotSelection(new DateOnly(2024, 12, 31), new DateOnly(2024, 1, 1), 0, ["MA/2024/001", " "])
            .Validate(new string('n', 201));

        Assert.Equal(["name", "dateFrom", "session", "assemblyVoteIds"], errors.Select(e => e.Field));
    }

    [Fact]
    public void A_filter_or_a_list_alone_is_enough()
    {
        Assert.Empty(new SnapshotSelection(null, null, 79, []).Validate("Q1"));
        Assert.Empty(new SnapshotSelection(new DateOnly(2024, 1, 1), null, null, []).Validate("Q1"));
        Assert.Empty(new SnapshotSelection(null, null, null, ["MA/2024/001"]).Validate("Q1"));
    }

    [Fact]
    public void Listed_ids_are_trimmed_and_deduplicated() =>
        Assert.Equal(["MA/2024/001", "MA/2024/002"],
            new SnapshotSelection(null, null, null, [" MA/2024/001", "MA/2024/002", "MA/2024/001 "]).DistinctIds());
}
