namespace Meridian.Domain.Membership;

/// <summary>
/// All membership periods, loaded once per import (DECISIONS §2). A country is a member on date D if one of its
/// periods contains D; both ends are inclusive. A country that left and came back has two periods.
/// </summary>
public sealed class MembershipCalendar
{
    private readonly Dictionary<string, MembershipPeriod[]> _byCountry;

    public MembershipCalendar(IEnumerable<MembershipPeriod> periods)
    {
        _byCountry = periods
            .GroupBy(p => p.CountryCode)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.From).ToArray());
    }

    /// <summary>True if the code is a member state at all (has any period).</summary>
    public bool IsKnown(string countryCode) => _byCountry.ContainsKey(countryCode);

    public bool IsMember(string countryCode, DateOnly date) =>
        _byCountry.TryGetValue(countryCode, out var periods) && periods.Any(p => p.Contains(date));
}
