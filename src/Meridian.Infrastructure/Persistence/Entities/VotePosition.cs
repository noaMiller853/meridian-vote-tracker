namespace Meridian.Infrastructure.Persistence.Entities;

public class VotePosition
{
    public long VoteId { get; set; }

    /// <summary>Y or N.</summary>
    public string DesiredOutcome { get; set; } = null!;

    /// <summary>1 to 5.</summary>
    public short Weight { get; set; }

    public long SetById { get; set; }
    public DateTimeOffset SetAt { get; set; }

    public Vote Vote { get; set; } = null!;
    public User SetBy { get; set; } = null!;
}
