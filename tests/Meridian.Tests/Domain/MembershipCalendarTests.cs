using Meridian.Domain.Membership;

namespace Meridian.Tests.Domain;

/// <summary>Membership on a date, against the real seed file. Both ends of a period are inclusive.</summary>
public class MembershipCalendarTests
{
    private static readonly MembershipCalendar Calendar = Ingest.RealCalendar;

    [Theory]
    // Iceland left and came back: 1995-01-01..2019-12-31, then 2022-01-01..open. The gap is 2020-01-01..2021-12-31.
    [InlineData("ISL", "1995-01-01", true)]
    [InlineData("ISL", "2019-12-31", true)]
    [InlineData("ISL", "2020-01-01", false)]
    [InlineData("ISL", "2021-06-15", false)]
    [InlineData("ISL", "2021-12-31", false)]
    [InlineData("ISL", "2022-01-01", true)]
    [InlineData("ISL", "2024-02-28", true)]
    // New Zealand left after 2021-06-30.
    [InlineData("NZL", "2021-06-30", true)]
    [InlineData("NZL", "2021-07-01", false)]
    [InlineData("NZL", "2024-02-28", false)]
    // Timor-Leste joined on 2023-01-01.
    [InlineData("TLS", "2022-12-31", false)]
    [InlineData("TLS", "2023-01-01", true)]
    // Before anyone was a member.
    [InlineData("FRA", "1994-12-31", false)]
    [InlineData("FRA", "1995-01-01", true)]
    public void Member_on_date(string code, string date, bool expected)
    {
        Assert.Equal(expected, Calendar.IsMember(code, DateOnly.Parse(date)));
    }

    [Fact]
    public void Codes_with_a_gap_are_still_known()
    {
        Assert.True(Calendar.IsKnown("ISL"));
        Assert.True(Calendar.IsKnown("NZL"));
    }

    [Theory]
    [InlineData("FRN")]   // real: q1 line 2143
    [InlineData("fra")]   // the calendar is exact; normalization happens before
    [InlineData("")]
    public void Unknown_codes_are_never_members(string code)
    {
        Assert.False(Calendar.IsKnown(code));
        Assert.False(Calendar.IsMember(code, new DateOnly(2024, 1, 1)));
    }

    [Fact]
    public void One_day_period_contains_exactly_that_day()
    {
        var calendar = new MembershipCalendar([new MembershipPeriod("XYZ", new DateOnly(2024, 3, 1), new DateOnly(2024, 3, 1))]);

        Assert.False(calendar.IsMember("XYZ", new DateOnly(2024, 2, 29)));
        Assert.True(calendar.IsMember("XYZ", new DateOnly(2024, 3, 1)));
        Assert.False(calendar.IsMember("XYZ", new DateOnly(2024, 3, 2)));
    }
}
