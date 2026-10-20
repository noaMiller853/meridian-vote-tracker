namespace Meridian.Infrastructure.Persistence.Entities;

public class Snapshot
{
    public const string StatusCalculating = "Calculating";
    public const string StatusSaved = "Saved";
    public const string StatusFailed = "Failed";
    public const string StatusPublished = "Published";

    public long Id { get; set; }
    public string Name { get; set; } = null!;

    /// <summary>Vote selection used (jsonb).</summary>
    public string Filter { get; set; } = null!;

    /// <summary>Scoring rules used (jsonb).</summary>
    public string Rules { get; set; } = null!;

    /// <summary>
    /// The creator confirmed that votes without a position are left out (POS-04); without it, creation is refused
    /// while the selection has such votes.
    /// </summary>
    public bool ExcludeWithoutPosition { get; set; }

    /// <summary>Calculating, Saved, Failed or Published. Un-publishing returns to Saved.</summary>
    public string Status { get; set; } = null!;

    /// <summary>Quarter of the latest included vote date, for example 2024-Q1; set when Saved (SNP-05).</summary>
    public string? Quarter { get; set; }

    public long CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long? BackgroundJobId { get; set; }
    public long? PublishedById { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>The snapshot that was published for the same quarter before this one replaced it.</summary>
    public long? ReplacedSnapshotId { get; set; }

    public User CreatedBy { get; set; } = null!;
    public User? PublishedBy { get; set; }
    public BackgroundJob? BackgroundJob { get; set; }
    public Snapshot? ReplacedSnapshot { get; set; }
    public List<SnapshotVote> Votes { get; set; } = [];
    public List<SnapshotScore> Scores { get; set; } = [];
}
