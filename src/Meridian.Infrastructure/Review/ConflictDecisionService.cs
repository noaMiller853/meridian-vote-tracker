using System.Globalization;
using System.Text.Json;
using Meridian.Domain.Ingestion;
using Meridian.Domain.Review;
using Meridian.Infrastructure.Persistence;
using Meridian.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Meridian.Infrastructure.Review;

public sealed record DecisionRequest(Decision Decision, string? Note, string? ExpectedApprovedValue);

public enum DecisionResultKind { Applied, NotFound, AlreadyDecided, Stale }

/// <param name="Conflict">The conflict after the decision, or its current state when nothing was applied.</param>
public sealed record DecisionResult(DecisionResultKind Kind, ConflictDetails? Conflict);

/// <summary>
/// CON-04 and CON-05: one decision on one conflict. The conditional <c>UPDATE ... WHERE status = 'Pending'</c> decides
/// who wins; the approved-value change and the audit events are in the same transaction (DECISIONS §5).
/// </summary>
public sealed class ConflictDecisionService(MeridianDbContext db, ConflictReader reader, ILogger<ConflictDecisionService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<DecisionResult> DecideAsync(long id, DecisionRequest request, long userId, string correlationId, CancellationToken ct)
    {
        // The parts of a conflict that never change; its status is read under the conditional update below.
        var c = await db.Conflicts.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new
            {
                x.Kind, x.VoteId, x.CountryCode, x.FieldName, x.ApprovedValue, x.ProposedValue,
                x.ProposedSourceImportId, x.ProposedSourceRowNumber,
            })
            .SingleOrDefaultAsync(ct);
        if (c is null)
            return new DecisionResult(DecisionResultKind.NotFound, null);

        var approve = request.Decision == Decision.Approve;
        var status = approve ? ConflictReader.StatusApproved : ConflictReader.StatusRejected;
        var now = DateTimeOffset.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Approve: lock the approved value first, so two approvals on the same target are serialized and the value
        // read here is the one that gets replaced. Lock order is always approved value, then conflict.
        Approved? current = approve ? await LockApprovedAsync(c.Kind, c.VoteId, c.CountryCode, c.FieldName, ct) : null;
        var replacedValue = current?.Value;
        var replacedSource = current?.SourceImportId;

        // CON-05: only one decision can move the conflict out of Pending. A second decider updates 0 rows.
        var updated = await db.Conflicts
            .Where(x => x.Id == id && x.Status == ConflictDecision.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.DecidedById, userId)
                .SetProperty(x => x.DecidedAt, now)
                .SetProperty(x => x.DecisionNote, request.Note)
                .SetProperty(x => x.ReplacedValue, replacedValue)
                .SetProperty(x => x.ReplacedSourceImportId, replacedSource), ct);

        var statusNow = updated == 1 ? ConflictDecision.Pending
            : await db.Conflicts.AsNoTracking().Where(x => x.Id == id).Select(x => x.Status).SingleAsync(ct);
        var outcome = ConflictDecision.Evaluate(statusNow, request.Decision, c.ApprovedValue,
            current?.Value ?? c.ApprovedValue, request.ExpectedApprovedValue);

        if (outcome != DecisionOutcome.Apply)
        {
            await tx.RollbackAsync(ct);
            if (outcome == DecisionOutcome.AlreadyDecided)
                await AuditLosingAttemptAsync(id, c.VoteId, request, userId, correlationId, ct);
            logger.LogInformation("Decision {Decision} on conflict {ConflictId} not applied: {Outcome}.", request.Decision, id, outcome);
            return new DecisionResult(outcome == DecisionOutcome.Stale ? DecisionResultKind.Stale : DecisionResultKind.AlreadyDecided,
                await reader.GetAsync(id, ct));
        }

        if (approve)
            await ApplyAsync(c.Kind, c.VoteId, c.CountryCode, c.FieldName, c.ProposedValue, c.ProposedSourceImportId,
                c.ProposedSourceRowNumber, userId, now, ct);

        var target = c.CountryCode ?? c.FieldName!;
        db.AuditEvents.Add(Audit("conflict", id.ToString(CultureInfo.InvariantCulture), c.VoteId,
            approve ? "ConflictApproved" : "ConflictRejected",
            new { status = ConflictDecision.Pending, approvedValue = c.ApprovedValue },
            new { status, kind = c.Kind, target, approvedValue = current?.Value ?? c.ApprovedValue, proposedValue = c.ProposedValue, note = request.Note },
            userId, c.ProposedSourceImportId, correlationId, now));
        if (approve)
            db.AuditEvents.Add(c.Kind == "CountryVote"
                ? Audit("country_vote", $"{c.VoteId}:{c.CountryCode}", c.VoteId, "BallotChanged",
                    new { countryCode = c.CountryCode, ballot = current!.Value, sourceImportId = current.SourceImportId },
                    new { countryCode = c.CountryCode, ballot = c.ProposedValue, sourceImportId = c.ProposedSourceImportId, conflictId = id },
                    userId, c.ProposedSourceImportId, correlationId, now)
                : Audit("vote", c.VoteId.ToString(CultureInfo.InvariantCulture), c.VoteId, "VoteFieldChanged",
                    new { field = c.FieldName, value = current!.Value, sourceImportId = current.SourceImportId },
                    new { field = c.FieldName, value = c.ProposedValue, sourceImportId = c.ProposedSourceImportId, conflictId = id },
                    userId, c.ProposedSourceImportId, correlationId, now));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogInformation("Conflict {ConflictId} {Status} ({Kind} {Target}: {Approved} -> {Proposed}).",
            id, status, c.Kind, target, current?.Value ?? c.ApprovedValue, c.ProposedValue);
        return new DecisionResult(DecisionResultKind.Applied, await reader.GetAsync(id, ct));
    }

    private sealed record Approved(string Value, long SourceImportId);

    private async Task<Approved> LockApprovedAsync(string kind, long voteId, string? countryCode, string? field, CancellationToken ct)
    {
        if (kind == "CountryVote")
        {
            var ballot = await db.CountryVotes
                .FromSql($"SELECT * FROM country_votes WHERE vote_id = {voteId} AND country_code = {countryCode} FOR UPDATE")
                .AsNoTracking().SingleAsync(ct);
            return new Approved(ballot.Ballot, ballot.SourceImportId);
        }

        var vote = await db.Votes.FromSql($"SELECT * FROM votes WHERE id = {voteId} FOR UPDATE").AsNoTracking().SingleAsync(ct);
        var source = field switch
        {
            VoteFields.Session => vote.SessionSourceImportId,
            VoteFields.VoteDate => vote.VoteDateSourceImportId,
            _ => vote.TitleSourceImportId,
        };
        return new Approved(ConflictReader.FieldValue(field!, vote.Session, vote.VoteDate, vote.Title), source);
    }

    /// <summary>The proposed value becomes approved; it now comes from the proposing import (HIS-02).</summary>
    private async Task ApplyAsync(string kind, long voteId, string? countryCode, string? field, string proposed,
        long sourceImportId, int sourceRow, long userId, DateTimeOffset now, CancellationToken ct)
    {
        int updated;
        if (kind == "CountryVote")
            updated = await db.CountryVotes.Where(v => v.VoteId == voteId && v.CountryCode == countryCode)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.Ballot, proposed)
                    .SetProperty(v => v.SourceImportId, sourceImportId)
                    .SetProperty(v => v.SourceRowNumber, sourceRow)
                    .SetProperty(v => v.ApprovedById, userId)
                    .SetProperty(v => v.ApprovedAt, now), ct);
        else
        {
            var votes = db.Votes.Where(v => v.Id == voteId);
            updated = field switch
            {
                VoteFields.Session => await votes.ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.Session, ParseSession(proposed))
                    .SetProperty(v => v.SessionSourceImportId, sourceImportId), ct),
                VoteFields.VoteDate => await votes.ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.VoteDate, ParseDate(proposed))
                    .SetProperty(v => v.VoteDateSourceImportId, sourceImportId), ct),
                VoteFields.Title => await votes.ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.Title, proposed)
                    .SetProperty(v => v.TitleSourceImportId, sourceImportId), ct),
                _ => throw new InvalidOperationException($"Unknown vote field '{field}'."),
            };
        }

        // The row is locked by this transaction, so it cannot have disappeared.
        if (updated != 1)
            throw new InvalidOperationException($"The approved value of {kind} {voteId}/{countryCode ?? field} was not updated.");
    }

    // Conflicts store values as text in the form the import compared them (DECISIONS §1).
    private static int ParseSession(string value) => int.Parse(value, CultureInfo.InvariantCulture);
    private static DateOnly ParseDate(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>DECISIONS §4: a decision that lost the race is kept in the vote timeline, in its own transaction.</summary>
    private async Task AuditLosingAttemptAsync(long id, long voteId, DecisionRequest request, long userId, string correlationId, CancellationToken ct)
    {
        var winner = await db.Conflicts.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.Status, DecidedBy = x.DecidedBy == null ? null : x.DecidedBy.Name, x.DecidedAt })
            .SingleAsync(ct);
        db.ChangeTracker.Clear();
        db.AuditEvents.Add(Audit("conflict", id.ToString(CultureInfo.InvariantCulture), voteId, "DecisionAttemptRejected",
            new { status = winner.Status, decidedBy = winner.DecidedBy, decidedAt = winner.DecidedAt },
            new { attempted = request.Decision.ToString(), note = request.Note },
            userId, null, correlationId, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(ct);
    }

    private static AuditEvent Audit(string entity, string entityId, long voteId, string action, object? oldValue, object newValue,
        long userId, long? importId, string correlationId, DateTimeOffset at) => new()
    {
        Entity = entity,
        EntityId = entityId,
        VoteId = voteId,
        Action = action,
        OldValue = oldValue is null ? null : JsonSerializer.Serialize(oldValue, Json),
        NewValue = JsonSerializer.Serialize(newValue, Json),
        ActorId = userId,
        ImportId = importId,
        CorrelationId = correlationId,
        OccurredAt = at,
    };
}
