using System.Globalization;
using System.Text.Json;
using Meridian.Domain.Positions;
using Meridian.Infrastructure.Persistence;
using Meridian.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Meridian.Infrastructure.Votes;

public enum PositionResultKind { Changed, Unchanged, NotFound, VersionMismatch }

/// <param name="Position">The position after the request, or the current one when nothing was written (null: none).</param>
public sealed record SetPositionResult(PositionResultKind Kind, PositionDto? Position);

/// <summary>
/// POS-01..POS-03: the Admin sets the stance and weight of a vote. The write, the version check and the audit event are
/// one transaction; a change based on an old version writes nothing (DECISIONS §11).
/// </summary>
public sealed class PositionService(MeridianDbContext db, ILogger<PositionService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SetPositionResult> SetAsync(long voteId, Position requested, int expectedVersion, long userId,
        string correlationId, CancellationToken ct)
    {
        if (!await db.Votes.AnyAsync(v => v.Id == voteId, ct))
            return new SetPositionResult(PositionResultKind.NotFound, null);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Lock the current position, so two changes of one vote are serialized and the version read is the one compared.
        var current = await db.VotePositions
            .FromSql($"SELECT * FROM vote_positions WHERE vote_id = {voteId} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct);
        var outcome = PositionRules.Decide(requested, expectedVersion, ToDomain(current), current?.Version ?? 0);

        var now = DateTimeOffset.UtcNow;
        var desired = PositionRules.ToDesiredOutcome(requested.Stance);
        var weight = (short)requested.Weight;
        if (outcome == PositionOutcome.Create)
        {
            // No row to lock yet: a concurrent first position waits on the primary key and then inserts nothing.
            var inserted = await db.Database.ExecuteSqlAsync($"""
                INSERT INTO vote_positions (vote_id, desired_outcome, weight, set_by_id, set_at, version)
                VALUES ({voteId}, {desired}, {weight}, {userId}, {now}, 1)
                ON CONFLICT (vote_id) DO NOTHING
                """, ct);
            if (inserted == 0)
            {
                await tx.RollbackAsync(ct);
                // Someone else set the first position: same values are fine, anything else is a mismatch.
                var winner = await db.VotePositions.AsNoTracking().SingleAsync(p => p.VoteId == voteId, ct);
                outcome = PositionRules.Decide(requested, expectedVersion, ToDomain(winner), winner.Version);
                return await NotWrittenAsync(voteId, outcome, ct);
            }
        }
        else if (outcome == PositionOutcome.Update)
        {
            await db.VotePositions.Where(p => p.VoteId == voteId && p.Version == expectedVersion)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.DesiredOutcome, desired)
                    .SetProperty(p => p.Weight, weight)
                    .SetProperty(p => p.SetById, userId)
                    .SetProperty(p => p.SetAt, now)
                    .SetProperty(p => p.Version, p => p.Version + 1), ct);
        }
        else
        {
            await tx.RollbackAsync(ct);
            return await NotWrittenAsync(voteId, outcome, ct);
        }

        // HIS-03 / POS-03: who, when, previous value and new value.
        var newVersion = (current?.Version ?? 0) + 1;
        db.AuditEvents.Add(new AuditEvent
        {
            Entity = "vote_position",
            EntityId = voteId.ToString(CultureInfo.InvariantCulture),
            VoteId = voteId,
            Action = current is null ? "PositionSet" : "PositionChanged",
            OldValue = current is null ? null : JsonSerializer.Serialize(new
            {
                stance = PositionRules.FromDesiredOutcome(current.DesiredOutcome).ToString(),
                weight = (int)current.Weight,
                version = current.Version,
            }, Json),
            NewValue = JsonSerializer.Serialize(new { stance = requested.Stance.ToString(), weight = requested.Weight, version = newVersion }, Json),
            ActorId = userId,
            CorrelationId = correlationId,
            OccurredAt = now,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation("Position of vote {VoteId} set to {Stance}, weight {Weight} (version {Version}).",
            voteId, requested.Stance, requested.Weight, newVersion);
        return new SetPositionResult(PositionResultKind.Changed, await ReadAsync(voteId, ct));
    }

    private async Task<SetPositionResult> NotWrittenAsync(long voteId, PositionOutcome outcome, CancellationToken ct)
    {
        var kind = outcome == PositionOutcome.Unchanged ? PositionResultKind.Unchanged : PositionResultKind.VersionMismatch;
        logger.LogInformation("Position of vote {VoteId} not written: {Outcome}.", voteId, outcome);
        return new SetPositionResult(kind, await ReadAsync(voteId, ct));
    }

    private Task<PositionDto?> ReadAsync(long voteId, CancellationToken ct) =>
        db.VotePositions.AsNoTracking().Where(p => p.VoteId == voteId)
            .Select(p => new PositionDto(p.DesiredOutcome == "Y" ? "For" : "Against", p.Weight, p.Version, p.SetBy.Name, p.SetAt))
            .SingleOrDefaultAsync(ct);

    private static Position? ToDomain(VotePosition? p) =>
        p is null ? null : new Position(PositionRules.FromDesiredOutcome(p.DesiredOutcome), p.Weight);
}
