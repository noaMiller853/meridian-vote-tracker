namespace Meridian.Infrastructure.Persistence.Entities;

public class BackgroundJob
{
    public long Id { get; set; }

    /// <summary>Import or Snapshot.</summary>
    public string Type { get; set; } = null!;

    /// <summary>Queued, Running, Succeeded or Failed.</summary>
    public string Status { get; set; } = null!;

    public int Done { get; set; }
    public int? Total { get; set; }
    public string? Error { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
