namespace Meridian.Infrastructure.Persistence.Entities;

public class User
{
    public long Id { get; set; }
    public string Name { get; set; } = null!;

    /// <summary>Analyst or Admin.</summary>
    public string Role { get; set; } = null!;
}
