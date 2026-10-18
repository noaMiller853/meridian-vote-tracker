namespace Meridian.Infrastructure.Persistence.Entities;

public class CountryMembership
{
    public long Id { get; set; }
    public string CountryCode { get; set; } = null!;
    public DateOnly MemberFrom { get; set; }

    /// <summary>Null means still a member.</summary>
    public DateOnly? MemberTo { get; set; }

    public Country Country { get; set; } = null!;
}
