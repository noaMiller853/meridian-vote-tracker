using Meridian.Domain.Membership;

namespace Meridian.Tests.Domain;

public class MembershipRulesTests
{
    private static MembershipPeriod P(string code, string from, string? to) =>
        new(code, DateOnly.Parse(from), to is null ? null : DateOnly.Parse(to));

    [Fact]
    public void Left_and_came_back_is_valid()
    {
        // Real data: Iceland.
        var periods = new[] { P("ISL", "1995-01-01", "2019-12-31"), P("ISL", "2022-01-01", null) };

        Assert.Empty(MembershipRules.FindOverlaps(periods));
    }

    [Fact]
    public void Adjacent_periods_do_not_overlap()
    {
        Assert.Empty(MembershipRules.FindOverlaps([P("FRA", "1995-01-01", "2019-12-31"), P("FRA", "2020-01-01", null)]));
    }

    [Fact]
    public void Shared_boundary_day_overlaps_because_both_ends_are_inclusive()
    {
        Assert.Equal([(0, 1)], MembershipRules.FindOverlaps([P("FRA", "1995-01-01", "2019-12-31"), P("FRA", "2019-12-31", null)]));
    }

    [Fact]
    public void Open_ended_period_overlaps_any_later_period()
    {
        Assert.Equal([(0, 1)], MembershipRules.FindOverlaps([P("FRA", "1995-01-01", null), P("FRA", "2030-01-01", "2031-01-01")]));
    }

    [Fact]
    public void Identical_rows_overlap()
    {
        Assert.Equal([(0, 1)], MembershipRules.FindOverlaps([P("FRA", "1995-01-01", null), P("FRA", "1995-01-01", null)]));
    }

    [Fact]
    public void Different_countries_never_overlap()
    {
        Assert.Empty(MembershipRules.FindOverlaps([P("FRA", "1995-01-01", null), P("DEU", "1995-01-01", null)]));
    }

    [Fact]
    public void All_overlapping_pairs_are_reported_with_original_indexes()
    {
        var periods = new[]
        {
            P("DEU", "1995-01-01", null),
            P("FRA", "1995-01-01", "2010-12-31"),
            P("FRA", "2000-01-01", "2005-12-31"),
            P("FRA", "2008-01-01", null),
        };

        Assert.Equal([(1, 2), (1, 3)], MembershipRules.FindOverlaps(periods));
    }

    [Theory]
    [InlineData("2020-01-01", "2019-12-31", false)]
    [InlineData("2020-01-01", "2020-01-01", true)]
    [InlineData("2020-01-01", null, true)]
    public void End_before_start_is_invalid(string from, string? to, bool valid)
    {
        Assert.Equal(valid, P("FRA", from, to).IsValid);
    }
}
