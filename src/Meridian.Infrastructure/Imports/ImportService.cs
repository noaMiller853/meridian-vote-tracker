using System.Text.Json;
using Meridian.Infrastructure.Files;
using Meridian.Infrastructure.Persistence;
using Meridian.Infrastructure.Persistence.Entities;
using Microsoft.Extensions.Logging;

namespace Meridian.Infrastructure.Imports;

public sealed record ImportCreated(long ImportId, long JobId);

/// <summary>The upload request (ING-01): store the file, queue the job, return at once.</summary>
public sealed class ImportService(MeridianDbContext db, IFileStore files, ImportSignal signal, ILogger<ImportService> logger)
{
    public async Task<ImportCreated> CreateAsync(Stream content, string fileName, long userId, string correlationId, CancellationToken ct)
    {
        // Before the transaction: harmless if it fails afterwards, because the store is content-addressed.
        var stored = await files.SaveAsync(content, ct);
        var name = Path.GetFileName(fileName);
        var now = DateTimeOffset.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var import = new Import
        {
            Kind = Import.KindVotes,
            FileName = string.IsNullOrWhiteSpace(name) ? "upload.csv" : name,
            StorageUri = stored.Uri,
            Sha256 = stored.Sha256,
            UploadedById = userId,
            UploadedAt = now,
            BackgroundJob = new BackgroundJob
            {
                Type = BackgroundJob.TypeImport,
                Status = BackgroundJob.StatusQueued,
                CorrelationId = correlationId,
                CreatedAt = now,
            },
        };
        db.Imports.Add(import);
        await db.SaveChangesAsync(ct);

        db.AuditEvents.Add(new AuditEvent
        {
            Entity = "import",
            EntityId = import.Id.ToString(),
            Action = "Uploaded",
            NewValue = JsonSerializer.Serialize(new { fileName = import.FileName, sha256 = stored.Sha256, bytes = stored.Length }),
            ActorId = userId,
            ImportId = import.Id,
            CorrelationId = correlationId,
            OccurredAt = now,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation("Import {ImportId} queued as job {JobId}: {FileName}, {Bytes} bytes, sha256 {Sha256}.",
            import.Id, import.BackgroundJobId, import.FileName, stored.Length, stored.Sha256);
        signal.Notify();
        return new ImportCreated(import.Id, import.BackgroundJobId!.Value);
    }
}
