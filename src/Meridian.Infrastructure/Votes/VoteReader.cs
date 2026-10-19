using System.Text.Json;
using Meridian.Domain.Review;
using Meridian.Infrastructure.Persistence;
using Meridian.Infrastructure.Startup;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Infrastructure.Votes;

/// <param name="Text">Case-insensitive "contains" on the title or the Assembly id.</param>
/// <param name="DateFrom">Inclusive.</param>
/// <param name="DateTo">Inclusive.</param>
public sealed record VoteFilter(
    string? AssemblyVoteId, string? Text, int? Session, DateOnly? DateFrom, DateOnly? DateTo, bool? HasPosition, long AfterId, int Take);

/// <summary>The Votes screens (section 6): list, one vote, and its timeline (HIS-04). Everyone may read.</summary>
public sealed class VoteReader(MeridianDbContext db)
{
    public async Task<VotePage> ListAsync(VoteFilter filter, CancellationToken ct)
    {
        var query = db.Votes.AsNoTracking();
        if (filter.AssemblyVoteId is not null)
            query = query.Where(v => v.AssemblyVoteId == filter.AssemblyVoteId);
        if (filter.Text is not null)
        {
            var pattern = "%" + EscapeLike(filter.Text) + "%";
            query = query.Where(v => EF.Functions.ILike(v.Title, pattern, @"\") || EF.Functions.ILike(v.AssemblyVoteId, pattern, @"\"));
        }
        if (filter.Session is not null)
            query = query.Where(v => v.Session == filter.Session);
        if (filter.DateFrom is not null)
            query = query.Where(v => v.VoteDate >= filter.DateFrom);
        if (filter.DateTo is not null)
            query = query.Where(v => v.VoteDate <= filter.DateTo);
        if (filter.HasPosition is { } hasPosition)
            query = query.Where(v => (v.Position != null) == hasPosition);

        var total = await query.CountAsync(ct);
        var items = await query.Where(v => v.Id > filter.AfterId).OrderBy(v => v.Id).Take(filter.Take + 1)
            .Select(v => new VoteSummary(
                v.Id, v.AssemblyVoteId, v.Session, v.VoteDate, v.Title,
                v.CountryVotes.Count,
                v.Position != null,
                v.Position == null ? null
                    : new PositionDto(v.Position.DesiredOutcome == "Y" ? "For" : "Against", v.Position.Weight, v.Position.Version,
                        v.Position.SetBy.Name, v.Position.SetAt),
                v.Conflicts.Count(c => c.Status == ConflictDecision.Pending)))
            .ToListAsync(ct);

        var more = items.Count > filter.Take;
        if (more)
            items.RemoveAt(items.Count - 1);
        return new VotePage(items, total, more ? items[^1].Id : null);
    }

    public async Task<VoteView?> GetAsync(long id, CancellationToken ct)
    {
        var vote = await db.Votes.AsNoTracking().Where(v => v.Id == id)
            .Select(v => new
            {
                v.Id, v.AssemblyVoteId, v.Session, v.VoteDate, v.Title, v.CreatedAt,
                FirstImport = new ImportRef(v.FirstImportId, v.FirstImport.FileName),
                Sources = new VoteDetailSources(
                    new ImportRef(v.SessionSourceImportId, v.SessionSourceImport.FileName),
                    new ImportRef(v.VoteDateSourceImportId, v.VoteDateSourceImport.FileName),
                    new ImportRef(v.TitleSourceImportId, v.TitleSourceImport.FileName)),
                Position = v.Position == null ? null
                    : new PositionDto(v.Position.DesiredOutcome == "Y" ? "For" : "Against", v.Position.Weight, v.Position.Version,
                        v.Position.SetBy.Name, v.Position.SetAt),
                PendingConflicts = v.Conflicts.Count(c => c.Status == ConflictDecision.Pending),
            })
            .SingleOrDefaultAsync(ct);
        if (vote is null)
            return null;

        var ballots = await db.CountryVotes.AsNoTracking().Where(c => c.VoteId == id).OrderBy(c => c.CountryCode)
            .Select(c => new CountryVoteDto(
                c.CountryCode, c.Country.Name, c.Ballot,
                new ImportRef(c.SourceImportId, c.SourceRow.Import.FileName), c.SourceRowNumber,
                c.ApprovedBy.Name, c.ApprovedAt,
                // Only a conflict decision sets a human approver; ingested ballots are approved by system.
                c.ApprovedBy.Name != DatabaseInitializer.SystemUserName))
            .ToListAsync(ct);

        return new VoteView(vote.Id, vote.AssemblyVoteId, vote.Session, vote.VoteDate, vote.Title, vote.FirstImport, vote.Sources,
            vote.CreatedAt, vote.Position is not null, vote.Position, vote.PendingConflicts, ballots);
    }

    /// <summary>
    /// HIS-04: the audit events of the vote plus one "Imported" entry per import that contained it (from import_votes),
    /// so an import that changed nothing still shows. Oldest first. All times come from the API's clock: an import's
    /// upload time comes before the events of its processing.
    /// </summary>
    public async Task<VoteTimeline?> TimelineAsync(long id, CancellationToken ct)
    {
        var vote = await db.Votes.AsNoTracking().Where(v => v.Id == id).Select(v => new { v.Id, v.AssemblyVoteId }).SingleOrDefaultAsync(ct);
        if (vote is null)
            return null;

        var events = await db.AuditEvents.AsNoTracking().Where(e => e.VoteId == id)
            .Select(e => new
            {
                e.Id, e.OccurredAt, e.Action, e.Entity, e.EntityId, Actor = e.Actor.Name, e.OldValue, e.NewValue,
                Import = e.Import == null ? null : new ImportRef(e.Import.Id, e.Import.FileName),
            })
            .ToListAsync(ct);
        var imports = await db.ImportVotes.AsNoTracking().Where(i => i.VoteId == id)
            .Select(i => new
            {
                i.ImportId, i.Import.FileName, i.Import.UploadedAt, Uploader = i.Import.UploadedBy.Name, i.VoteCreated,
                i.RowCount, i.NewCount, i.UnchangedCount, i.DuplicateCount, i.InvalidCount, i.ContradictionCount, i.ConflictCount,
                i.ConflictsOpened, i.ConflictsAlreadyPending, i.ConflictsAlreadyRejected, i.ConflictsAlreadySuperseded,
            })
            .ToListAsync(ct);

        var entries = new List<(DateTimeOffset At, int Rank, long Id, TimelineEntry Entry)>();
        foreach (var i in imports)
        {
            var counts = new Dictionary<string, int>
            {
                ["rows"] = i.RowCount, ["new"] = i.NewCount, ["unchanged"] = i.UnchangedCount, ["duplicate"] = i.DuplicateCount,
                ["invalid"] = i.InvalidCount, ["contradiction"] = i.ContradictionCount, ["conflict"] = i.ConflictCount,
                ["conflictsOpened"] = i.ConflictsOpened, ["conflictsAlreadyPending"] = i.ConflictsAlreadyPending,
                ["conflictsAlreadyRejected"] = i.ConflictsAlreadyRejected, ["conflictsAlreadySuperseded"] = i.ConflictsAlreadySuperseded,
            };
            entries.Add((i.UploadedAt, 0, i.ImportId, new TimelineEntry(i.UploadedAt, "Imported", i.Uploader,
                new ImportRef(i.ImportId, i.FileName), null, TimelineText.Imported(i.FileName, i.Uploader, counts),
                null, JsonSerializer.SerializeToElement(counts))));
        }
        foreach (var e in events)
        {
            var oldValue = Parse(e.OldValue);
            var newValue = Parse(e.NewValue);
            long? conflictId = e.Entity == "conflict" ? long.Parse(e.EntityId, System.Globalization.CultureInfo.InvariantCulture)
                : Long(newValue, "conflictId");
            entries.Add((e.OccurredAt, 1, e.Id, new TimelineEntry(e.OccurredAt, e.Action, e.Actor, e.Import, conflictId,
                TimelineText.Describe(e.Action, e.Actor, e.Import?.FileName, conflictId, oldValue, newValue), oldValue, newValue)));
        }

        return new VoteTimeline(vote.Id, vote.AssemblyVoteId,
            entries.OrderBy(x => x.At).ThenBy(x => x.Rank).ThenBy(x => x.Id).Select(x => x.Entry).ToList());
    }

    private static JsonElement? Parse(string? json)
    {
        if (json is null) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static long? Long(JsonElement? value, string name) =>
        value is { ValueKind: JsonValueKind.Object } v && v.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt64() : null;

    private static string EscapeLike(string text) =>
        text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
