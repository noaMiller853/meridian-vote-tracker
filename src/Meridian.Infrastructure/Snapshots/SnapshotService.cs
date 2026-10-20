using System.Globalization;
using System.Text.Json;
using Meridian.Domain.Snapshots;
using Meridian.Infrastructure.Persistence;
using Meridian.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Meridian.Infrastructure.Snapshots;

public enum CreateResultKind { Created, UnknownVotes, NoVotes, VotesWithoutPosition }

/// <param name="Votes">UnknownVotes: the ids that do not exist; VotesWithoutPosition: the first ones without a position.</param>
public sealed record CreateSnapshotResult(CreateResultKind Kind, long SnapshotId = 0, long JobId = 0, int Count = 0,
    IReadOnlyList<string>? Votes = null);

public enum PublishResultKind { Published, NotFound, NotSaved, QuarterTaken }

/// <param name="Snapshot">Published: the snapshot; NotSaved and QuarterTaken: its current state.</param>
/// <param name="Current">QuarterTaken: the snapshot that is published for the quarter.</param>
public sealed record PublishResult(PublishResultKind Kind, SnapshotDetails? Snapshot = null, SnapshotSummary? Current = null);

/// <summary>Requesting a snapshot (SNP-01, POS-04) and publishing one per quarter (SNP-05).</summary>
public sealed class SnapshotService(MeridianDbContext db, SnapshotReader reader, SnapshotSignal signal, ILogger<SnapshotService> logger)
{
    private const int MaxVotesListed = 50;

    public async Task<CreateSnapshotResult> CreateAsync(string name, SnapshotSelection selection, bool excludeWithoutPosition,
        long userId, string correlationId, CancellationToken ct)
    {
        var ids = selection.DistinctIds();
        if (ids.Count > 0)
        {
            var known = await db.Votes.Where(v => ids.Contains(v.AssemblyVoteId)).Select(v => v.AssemblyVoteId).ToListAsync(ct);
            var unknown = ids.Except(known).ToList();
            if (unknown.Count > 0)
                return new CreateSnapshotResult(CreateResultKind.UnknownVotes, Count: unknown.Count, Votes: unknown.Take(MaxVotesListed).ToList());
        }

        // The same selection the worker freezes: the filter's votes plus the listed ones.
        var hasFilter = selection.HasFilter;
        var (from, to, session) = (selection.DateFrom, selection.DateTo, selection.Session);
        var selected = db.Votes.Where(v =>
            (hasFilter && (from == null || v.VoteDate >= from) && (to == null || v.VoteDate <= to) && (session == null || v.Session == session))
            || ids.Contains(v.AssemblyVoteId));
        var total = await selected.CountAsync(ct);
        if (total == 0)
            return new CreateSnapshotResult(CreateResultKind.NoVotes);

        // POS-04: a vote without a position never leaves the snapshot silently. Without the creator's confirmation
        // nothing is created (the blocking of the example screen); with it they are listed as excluded.
        var withoutPosition = selected.Where(v => v.Position == null);
        var missing = await withoutPosition.CountAsync(ct);
        if (missing > 0 && !excludeWithoutPosition)
            return new CreateSnapshotResult(CreateResultKind.VotesWithoutPosition, Count: missing,
                Votes: await withoutPosition.OrderBy(v => v.VoteDate).ThenBy(v => v.AssemblyVoteId).Select(v => v.AssemblyVoteId)
                    .Take(MaxVotesListed).ToListAsync(ct));

        var now = DateTimeOffset.UtcNow;
        var stored = new StoredSelection(selection.DateFrom, selection.DateTo, selection.Session, ids);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var snapshot = new Snapshot
        {
            Name = name.Trim(),
            Filter = JsonSerializer.Serialize(stored, SnapshotCalculator.Json),
            Rules = JsonSerializer.Serialize(new { version = ScoringRules.Version, rules = ScoringRules.Description }, SnapshotCalculator.Json),
            ExcludeWithoutPosition = excludeWithoutPosition,
            Status = Snapshot.StatusCalculating,
            CreatedById = userId,
            CreatedAt = now,
            BackgroundJob = new BackgroundJob
            {
                Type = BackgroundJob.TypeSnapshot,
                Status = BackgroundJob.StatusQueued,
                CorrelationId = correlationId,
                CreatedAt = now,
            },
        };
        db.Snapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);
        db.AuditEvents.Add(Audit(snapshot.Id, "SnapshotRequested", null, new
        {
            name = snapshot.Name,
            selection = stored,
            excludeVotesWithoutPosition = excludeWithoutPosition,
            selectedVotes = total,
            votesWithoutPosition = missing,
        }, userId, correlationId, now));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation("Snapshot {SnapshotId} requested: {Votes} votes ({Missing} without a position).", snapshot.Id, total, missing);
        signal.Notify();
        return new CreateSnapshotResult(CreateResultKind.Created, snapshot.Id, snapshot.BackgroundJobId!.Value, total);
    }

    /// <summary>
    /// SNP-05. One published snapshot per quarter, enforced by ux_snapshots_published_quarter. With
    /// <paramref name="replace"/> the published one of the same quarter returns to Saved in the same transaction.
    /// </summary>
    public async Task<PublishResult> PublishAsync(long id, bool replace, long userId, string correlationId, CancellationToken ct)
    {
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var snapshot = await db.Snapshots.FromSql($"SELECT * FROM snapshots WHERE id = {id} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(ct);
            if (snapshot is null)
                return new PublishResult(PublishResultKind.NotFound);
            if (snapshot.Status != Snapshot.StatusSaved)
            {
                await tx.RollbackAsync(ct);
                return new PublishResult(PublishResultKind.NotSaved, await reader.GetAsync(id, ct));
            }

            var quarter = snapshot.Quarter!;
            var current = await db.Snapshots
                .FromSql($"SELECT * FROM snapshots WHERE quarter = {quarter} AND status = 'Published' FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(ct);
            if (current is not null && !replace)
            {
                await tx.RollbackAsync(ct);
                return await QuarterTakenAsync(id, quarter, ct);
            }

            var now = DateTimeOffset.UtcNow;
            if (current is not null)
            {
                await db.Snapshots.Where(s => s.Id == current.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, Snapshot.StatusSaved)
                    .SetProperty(x => x.PublishedById, (long?)null)
                    .SetProperty(x => x.PublishedAt, (DateTimeOffset?)null), ct);
                db.AuditEvents.Add(Audit(current.Id, "SnapshotUnpublished",
                    new { status = Snapshot.StatusPublished, quarter, publishedById = current.PublishedById, publishedAt = current.PublishedAt },
                    new { status = Snapshot.StatusSaved, quarter, replacedBy = id }, userId, correlationId, now));
            }

            var replaced = current?.Id;
            await db.Snapshots.Where(s => s.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, Snapshot.StatusPublished)
                .SetProperty(x => x.PublishedById, userId)
                .SetProperty(x => x.PublishedAt, now)
                .SetProperty(x => x.ReplacedSnapshotId, replaced), ct);
            db.AuditEvents.Add(Audit(id, "SnapshotPublished", new { status = Snapshot.StatusSaved },
                new { status = Snapshot.StatusPublished, quarter, replacedSnapshotId = replaced }, userId, correlationId, now));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            logger.LogInformation("Snapshot {SnapshotId} published for {Quarter}{Replaced}.", id, quarter,
                replaced is null ? "" : $", replacing snapshot {replaced}");
            return new PublishResult(PublishResultKind.Published, await reader.GetAsync(id, ct));
        }
        catch (Exception ex) when (IsQuarterTaken(ex))
        {
            // Another publish for the same quarter committed first (the unique index decided).
            db.ChangeTracker.Clear();
            var quarter = await db.Snapshots.Where(s => s.Id == id).Select(s => s.Quarter!).SingleAsync(ct);
            return await QuarterTakenAsync(id, quarter, ct);
        }
    }

    private async Task<PublishResult> QuarterTakenAsync(long id, string quarter, CancellationToken ct)
    {
        var current = await SnapshotReader.Summaries(db.Snapshots.Where(s => s.Quarter == quarter && s.Status == Snapshot.StatusPublished))
            .SingleOrDefaultAsync(ct);
        return new PublishResult(PublishResultKind.QuarterTaken, await reader.GetAsync(id, ct), current);
    }

    private static bool IsQuarterTaken(Exception ex) =>
        (ex as PostgresException ?? ex.InnerException as PostgresException) is { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && pg.ConstraintName == "ux_snapshots_published_quarter";

    private static AuditEvent Audit(long snapshotId, string action, object? oldValue, object newValue, long userId, string correlationId,
        DateTimeOffset at) => new()
    {
        Entity = "snapshot",
        EntityId = snapshotId.ToString(CultureInfo.InvariantCulture),
        Action = action,
        OldValue = oldValue is null ? null : JsonSerializer.Serialize(oldValue, SnapshotCalculator.Json),
        NewValue = JsonSerializer.Serialize(newValue, SnapshotCalculator.Json),
        ActorId = userId,
        CorrelationId = correlationId,
        OccurredAt = at,
    };
}
