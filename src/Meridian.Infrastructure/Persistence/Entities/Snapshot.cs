namespace Meridian.Infrastructure.Persistence.Entities;

public class Snapshot
{
    public long Id { get; set; }
    public string Name { get; set; } = null!;

    /// <summary>Vote selection used (jsonb).</summary>
    public string Filter { get; set; } = null!;

    /// <summary>Scoring rules used (jsonb).</summary>
    public string Rules { get; set; } = null!;

    /// <summary>Calculating, Saved, Failed or Published. Un-publishing returns to Saved.</summary>
    public string Status { get; set; } = null!;

    /// <summary>For example 2024-Q1. Required once Published.</summary>
    public string? Quarter { get; set; }

    public long CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long? BackgroundJobId { get; set; }
    public long? PublishedById { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }

    public User CreatedBy { get; set; } = null!;
    public User? PublishedBy { get; set; }
    public BackgroundJob? BackgroundJob { get; set; }
    public List<SnapshotVote> Votes { get; set; } = [];
    public List<SnapshotScore> Scores { get; set; } = [];
    public List<SnapshotContribution> Contributions { get; set; } = [];
}
