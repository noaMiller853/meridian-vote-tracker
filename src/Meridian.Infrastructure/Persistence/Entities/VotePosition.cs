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

    /// <summary>
    /// 1 when first set, +1 on every change. A change must name the version it was based on, so two admins never
    /// overwrite each other silently (DECISIONS §11). 0 in a request means "the vote has no position yet".
    /// </summary>
    public int Version { get; set; }

    public Vote Vote { get; set; } = null!;
    public User SetBy { get; set; } = null!;
}
