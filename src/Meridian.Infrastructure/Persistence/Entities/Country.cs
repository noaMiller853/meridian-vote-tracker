namespace Meridian.Infrastructure.Persistence.Entities;

public class Country
{
    public string CountryCode { get; set; } = null!;
    public string Name { get; set; } = null!;

    public List<CountryMembership> Memberships { get; set; } = [];
}
