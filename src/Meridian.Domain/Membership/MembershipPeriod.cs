namespace Meridian.Domain.Membership;

/// <summary>A period in which a country is a member. Both ends are inclusive; a null end means still a member.</summary>
public sealed record MembershipPeriod(string CountryCode, DateOnly From, DateOnly? To)
{
    public bool IsValid => To is null || To >= From;

    public bool Contains(DateOnly date) => From <= date && (To is null || date <= To);

    public bool Overlaps(MembershipPeriod other) =>
        CountryCode == other.CountryCode
        && From <= (other.To ?? DateOnly.MaxValue)
        && other.From <= (To ?? DateOnly.MaxValue);

    public override string ToString() => $"{CountryCode} {From:yyyy-MM-dd} to {To?.ToString("yyyy-MM-dd") ?? "open"}";
}
