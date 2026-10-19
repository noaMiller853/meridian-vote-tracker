namespace Meridian.Infrastructure.Persistence.Entities;

public class BackgroundJob
{
    public const string TypeImport = "Import";
    public const string TypeSnapshot = "Snapshot";

    public const string StatusQueued = "Queued";
    public const string StatusRunning = "Running";
    public const string StatusSucceeded = "Succeeded";
    public const string StatusFailed = "Failed";

    public const string PhaseReading = "Reading";
    public const string PhaseClassifying = "Classifying";
    public const string PhaseWriting = "Writing";

    public long Id { get; set; }

    /// <summary>Import or Snapshot.</summary>
    public string Type { get; set; } = null!;

    /// <summary>Queued, Running, Succeeded or Failed.</summary>
    public string Status { get; set; } = null!;

    /// <summary>What a Running job is doing: Reading (total not known yet), Classifying or Writing. Null otherwise.</summary>
    public string? Phase { get; set; }

    public int Done { get; set; }
    public int? Total { get; set; }
    public string? Error { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
