namespace Meridian.Infrastructure.Persistence.Entities;

public class Import
{
    public long Id { get; set; }
    public string FileName { get; set; } = null!;
    public string StorageUri { get; set; } = null!;
    public string Sha256 { get; set; } = null!;
    public long UploadedById { get; set; }
    public DateTimeOffset UploadedAt { get; set; }

    /// <summary>File-level publication time, shown in the import report and conflict cards. Per-row values stay in import_rows.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    public long? BackgroundJobId { get; set; }

    public int NewCount { get; set; }
    public int UnchangedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int InvalidCount { get; set; }
    public int ContradictionCount { get; set; }
    public int ConflictCount { get; set; }
    public int NormalizedCount { get; set; }

    /// <summary>Import report (jsonb).</summary>
    public string? Report { get; set; }

    public User UploadedBy { get; set; } = null!;
    public BackgroundJob? BackgroundJob { get; set; }
}
