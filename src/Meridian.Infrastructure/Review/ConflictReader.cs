using Meridian.Domain.Ingestion;
using Meridian.Domain.Review;
using Meridian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Infrastructure.Review;

public sealed record ConflictFilter(string? Status, string? AssemblyVoteId, string? Kind, long AfterId, int Take);

/// <summary>The review queue (CON-01) and one conflict side by side (CON-02). Decided conflicts stay readable (CON-06).</summary>
public sealed class ConflictReader(MeridianDbContext db)
{
    public const string StatusApproved = "Approved";
    public const string StatusRejected = "Rejected";
    public static readonly string[] Statuses = [ConflictDecision.Pending, StatusApproved, StatusRejected];
    public static readonly string[] Kinds = ["CountryVote", "VoteField"];

    public async Task<ConflictPage> ListAsync(ConflictFilter filter, CancellationToken ct)
    {
        var query = db.Conflicts.AsNoTracking();
        if (filter.Status is not null)
            query = query.Where(c => c.Status == filter.Status);
        if (filter.AssemblyVoteId is not null)
            query = query.Where(c => c.Vote.AssemblyVoteId == filter.AssemblyVoteId);
        if (filter.Kind is not null)
            query = query.Where(c => c.Kind == filter.Kind);

        var total = await query.CountAsync(ct);
        var items = await query.Where(c => c.Id > filter.AfterId).OrderBy(c => c.Id).Take(filter.Take + 1)
            .Select(c => new ConflictListItem(
                c.Id, c.Status, c.Kind, c.VoteId, c.Vote.AssemblyVoteId, c.CountryCode,
                c.Country == null ? null : c.Country.Name, c.FieldName, c.ApprovedValue, c.ProposedValue,
                c.ProposedSourceRow.Import.FileName, c.CreatedAt,
                c.DecidedBy == null ? null : c.DecidedBy.Name, c.DecidedAt))
            .ToListAsync(ct);

        var more = items.Count > filter.Take;
        if (more)
            items.RemoveAt(items.Count - 1);
        return new ConflictPage(items, total, more ? items[^1].Id : null);
    }

    public async Task<ConflictDetails?> GetAsync(long id, CancellationToken ct)
    {
        var c = await db.Conflicts.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id, x.Status, x.Kind, x.VoteId, x.CountryCode, CountryName = x.Country == null ? null : x.Country.Name,
                x.FieldName, x.ApprovedValue, x.ProposedValue, x.CreatedAt, x.DecisionNote, x.DecidedAt,
                DecidedBy = x.DecidedBy == null ? null : x.DecidedBy.Name,
                Vote = new ConflictVote(x.Vote.Id, x.Vote.AssemblyVoteId, x.Vote.Session, x.Vote.VoteDate, x.Vote.Title),
                // Inline projections: a helper method here would make EF load whole entities instead of translating.
                ApprovedSource = new ValueSource(x.ApprovedSourceImport.Id, x.ApprovedSourceImport.FileName,
                    x.ApprovedSourceImport.UploadedBy.Name, x.ApprovedSourceImport.UploadedAt, x.ApprovedSourceImport.PublishedAt),
                ProposedSource = new ValueSource(x.ProposedSourceRow.Import.Id, x.ProposedSourceRow.Import.FileName,
                    x.ProposedSourceRow.Import.UploadedBy.Name, x.ProposedSourceRow.Import.UploadedAt, x.ProposedSourceRow.Import.PublishedAt),
                x.ProposedSourceRowNumber,
                x.ReplacedValue,
                ReplacedSource = x.ReplacedSourceImport == null ? null
                    : new ValueSource(x.ReplacedSourceImport.Id, x.ReplacedSourceImport.FileName,
                        x.ReplacedSourceImport.UploadedBy.Name, x.ReplacedSourceImport.UploadedAt, x.ReplacedSourceImport.PublishedAt),
            })
            .SingleOrDefaultAsync(ct);
        if (c is null)
            return null;

        var current = await CurrentValueAsync(c.Kind, c.VoteId, c.CountryCode, c.FieldName, ct);
        // Approved values only change through approvals, so the latest approval of another conflict on the same target
        // set the current value. No time comparison with created_at: that is the database clock, decided_at the API's.
        var changedBy = !ConflictDecision.IsStale(c.ApprovedValue, current) ? null
            : await db.Conflicts.AsNoTracking()
                .Where(o => o.Id != c.Id && o.VoteId == c.VoteId && o.Kind == c.Kind && o.CountryCode == c.CountryCode &&
                            o.FieldName == c.FieldName && o.Status == StatusApproved)
                .OrderByDescending(o => o.DecidedAt).ThenByDescending(o => o.Id)
                .Select(o => new ApprovedChange(o.Id, o.ProposedValue, o.DecidedBy!.Name, o.DecidedAt!.Value))
                .FirstOrDefaultAsync(ct);
        var stale = c.Status == ConflictDecision.Pending && ConflictDecision.IsStale(c.ApprovedValue, current);

        return new ConflictDetails(
            c.Id, c.Status, c.Kind, c.Vote, c.CountryCode, c.CountryName, c.FieldName,
            new ConflictSide(c.ApprovedValue, c.ApprovedSource, null),
            new ConflictSide(c.ProposedValue, c.ProposedSource, c.ProposedSourceRowNumber),
            new CurrentApprovedValue(current, stale, changedBy),
            c.DecidedBy is null ? null
                : new ConflictDecisionDto(c.Status, c.DecidedBy, c.DecidedAt!.Value, c.DecisionNote, c.ReplacedValue, c.ReplacedSource),
            c.CreatedAt);
    }

    /// <summary>The approved value of a conflict's target now, as text (the form stored on conflicts).</summary>
    internal async Task<string> CurrentValueAsync(string kind, long voteId, string? countryCode, string? field, CancellationToken ct)
    {
        if (kind == "CountryVote")
            return await db.CountryVotes.AsNoTracking().Where(v => v.VoteId == voteId && v.CountryCode == countryCode)
                .Select(v => v.Ballot).SingleAsync(ct);

        var vote = await db.Votes.AsNoTracking().Where(v => v.Id == voteId)
            .Select(v => new { v.Session, v.VoteDate, v.Title }).SingleAsync(ct);
        return FieldValue(field!, vote.Session, vote.VoteDate, vote.Title);
    }

    internal static string FieldValue(string field, int session, DateOnly voteDate, string title) => field switch
    {
        VoteFields.Session => session.ToString(System.Globalization.CultureInfo.InvariantCulture),
        VoteFields.VoteDate => voteDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        VoteFields.Title => title,
        _ => throw new InvalidOperationException($"Unknown vote field '{field}'."),
    };
}
