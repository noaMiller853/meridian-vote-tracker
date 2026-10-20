using System.Text.Json;
using Meridian.Domain.Snapshots;
using Meridian.Infrastructure.Persistence;
using Meridian.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Infrastructure.Snapshots;

/// <param name="Status">Snapshot status: Calculating, Saved, Failed or Published.</param>
/// <param name="JobStatus">Queued, Running, Succeeded or Failed; with <paramref name="Phase"/>, <paramref name="Done"/> and <paramref name="Total"/> while it runs (SNP-02).</param>
/// <param name="Quarter">The quarter of the latest included vote date; set when Saved (SNP-05).</param>
public sealed record SnapshotSummary(
    long Id,
    string Name,
    string Status,
    string JobStatus,
    string? Phase,
    int Done,
    int? Total,
    string? Error,
    string? Quarter,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? PublishedBy,
    DateTimeOffset? PublishedAt);

public sealed record SnapshotSelectionDto(DateOnly? DateFrom, DateOnly? DateTo, int? Session, IReadOnlyList<string> AssemblyVoteIds);

public sealed record SnapshotRulesDto(string Version, IReadOnlyList<string> Rules);

public sealed record ExcludedVote(string AssemblyVoteId, int Session, DateOnly VoteDate, string Title, string Reason);

/// <param name="Rank">Null for a country without a score.</param>
/// <param name="RankText">"N of M", or "n/a".</param>
/// <param name="ScoreText">One decimal, or "n/a".</param>
public sealed record CountryRanking(
    string CountryCode,
    string CountryName,
    decimal? Score,
    string ScoreText,
    int? Rank,
    string RankText,
    int CountedWeight,
    int MatchedWeight,
    OutcomeCounts Votes);

public sealed record OutcomeCounts(int Matched, int NotMatched, int Abstained, int Absent, int NotAMember, int NoBallot);

/// <param name="ExcludedVotes">The first 100 excluded votes with the reason (POS-04); all of them through /votes?included=false.</param>
/// <param name="Ranking">All listed countries in rank order, then the countries without a score; empty until Saved.</param>
public sealed record SnapshotDetails(
    SnapshotSummary Snapshot,
    SnapshotSelectionDto Selection,
    bool ExcludeVotesWithoutPosition,
    SnapshotRulesDto Rules,
    int IncludedVotes,
    int ExcludedVoteCount,
    IReadOnlyList<ExcludedVote> ExcludedVotes,
    long? ReplacedSnapshotId,
    int RankedCountries,
    IReadOnlyList<CountryRanking> Ranking);

/// <param name="Ordinal">The place of the vote in the snapshot (character <c>ordinal</c> of the frozen strings).</param>
/// <param name="DesiredOutcome">For or Against, as frozen.</param>
/// <param name="Ballot">Y, N, A, X, or null when the country had no ballot or was not a member.</param>
/// <param name="Outcome">Matched, NotMatched, Abstained, Absent, NotAMember or NoBallot.</param>
public sealed record VoteContribution(
    int Ordinal,
    string AssemblyVoteId,
    int Session,
    DateOnly VoteDate,
    string Title,
    string DesiredOutcome,
    int Weight,
    string? Ballot,
    string Outcome,
    bool Counted,
    bool Matched);

/// <param name="NextAfterOrdinal">Pass as <c>afterOrdinal</c> for the next page; null on the last page.</param>
public sealed record CountryBreakdown(CountryRanking Country, int Votes, IReadOnlyList<VoteContribution> Items, int? NextAfterOrdinal);

/// <param name="Ordinal">Null for an excluded vote.</param>
public sealed record SnapshotVoteDto(long VoteId, int? Ordinal, string AssemblyVoteId, int Session, DateOnly VoteDate, string Title,
    string? DesiredOutcome, int? Weight, bool Included, string? Reason);

public sealed record SnapshotVotePage(IReadOnlyList<SnapshotVoteDto> Items, int Total, long? NextAfterVoteId);

/// <summary>Snapshots as stored: nothing is recalculated on reading (SNP-04).</summary>
public sealed class SnapshotReader(MeridianDbContext db)
{
    private const int MaxExcludedListed = 100;

    // Filter and sort on the entity first: EF cannot translate a filter on the constructed records.
    public static IQueryable<SnapshotSummary> Summaries(IQueryable<Snapshot> snapshots) => snapshots.AsNoTracking().Select(s => new SnapshotSummary(
        s.Id, s.Name, s.Status,
        s.BackgroundJob == null ? BackgroundJob.StatusSucceeded : s.BackgroundJob.Status,
        s.BackgroundJob == null ? null : s.BackgroundJob.Phase,
        s.BackgroundJob == null ? 0 : s.BackgroundJob.Done,
        s.BackgroundJob == null ? null : s.BackgroundJob.Total,
        s.BackgroundJob == null ? null : s.BackgroundJob.Error,
        s.Quarter, s.CreatedBy.Name, s.CreatedAt,
        s.PublishedBy == null ? null : s.PublishedBy.Name, s.PublishedAt));

    public Task<List<SnapshotSummary>> ListAsync(int take, CancellationToken ct) =>
        Summaries(db.Snapshots.OrderByDescending(s => s.Id).Take(take)).ToListAsync(ct);

    public async Task<SnapshotDetails?> GetAsync(long id, CancellationToken ct)
    {
        var summary = await Summaries(db.Snapshots.Where(s => s.Id == id)).SingleOrDefaultAsync(ct);
        if (summary is null)
            return null;
        var s = await db.Snapshots.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.Filter, x.Rules, x.ExcludeWithoutPosition, x.ReplacedSnapshotId }).SingleAsync(ct);

        var votes = db.SnapshotVotes.AsNoTracking().Where(v => v.SnapshotId == id);
        var included = await votes.CountAsync(v => v.Included, ct);
        var excludedCount = await votes.CountAsync(v => !v.Included, ct);
        var excluded = await votes.Where(v => !v.Included).OrderBy(v => v.VoteDate).ThenBy(v => v.AssemblyVoteId).Take(MaxExcludedListed)
            .Select(v => new ExcludedVote(v.AssemblyVoteId, v.Session, v.VoteDate, v.Title, v.Reason!)).ToListAsync(ct);

        var scores = await db.SnapshotScores.AsNoTracking().Where(c => c.SnapshotId == id)
            .Select(c => new
            {
                c.CountryCode, c.CountryName, c.Score, c.Rank, c.CountedWeight, c.MatchedWeight, c.MatchedVotes, c.NotMatchedVotes,
                c.AbstainedVotes, c.AbsentVotes, c.NotAMemberVotes, c.NoBallotVotes,
            }) // never the long strings here
            .ToListAsync(ct);
        var ranked = scores.Count(c => c.Rank is not null);
        var ranking = scores
            .OrderBy(c => c.Rank ?? int.MaxValue).ThenBy(c => c.CountryName, StringComparer.InvariantCulture)
            .Select(c => Ranking(c.CountryCode, c.CountryName, c.Score, c.Rank, ranked, c.CountedWeight, c.MatchedWeight,
                new OutcomeCounts(c.MatchedVotes, c.NotMatchedVotes, c.AbstainedVotes, c.AbsentVotes, c.NotAMemberVotes, c.NoBallotVotes)))
            .ToList();

        var selection = JsonSerializer.Deserialize<StoredSelection>(s.Filter, SnapshotCalculator.Json)!;
        using var rules = JsonDocument.Parse(s.Rules);
        return new SnapshotDetails(summary,
            new SnapshotSelectionDto(selection.DateFrom, selection.DateTo, selection.Session, selection.AssemblyVoteIds),
            s.ExcludeWithoutPosition,
            new SnapshotRulesDto(rules.RootElement.GetProperty("version").GetString()!,
                rules.RootElement.GetProperty("rules").EnumerateArray().Select(r => r.GetString()!).ToList()),
            included, excludedCount, excluded, s.ReplacedSnapshotId, ranked, ranking);
    }

    /// <summary>SNP-03: how one country's score was calculated, vote by vote, from the frozen strings.</summary>
    public async Task<CountryBreakdown?> CountryAsync(long id, string countryCode, int afterOrdinal, int take, CancellationToken ct)
    {
        var country = await db.SnapshotScores.AsNoTracking().Where(c => c.SnapshotId == id && c.CountryCode == countryCode)
            .SingleOrDefaultAsync(ct);
        if (country is null)
            return null;
        var ranked = await db.SnapshotScores.CountAsync(c => c.SnapshotId == id && c.Rank != null, ct);

        var page = await db.SnapshotVotes.AsNoTracking()
            .Where(v => v.SnapshotId == id && v.Included && v.Ordinal > afterOrdinal)
            .OrderBy(v => v.Ordinal).Take(take + 1)
            .Select(v => new { Ordinal = v.Ordinal!.Value, v.AssemblyVoteId, v.Session, v.VoteDate, v.Title, v.DesiredOutcome, v.Weight })
            .ToListAsync(ct);
        var more = page.Count > take;
        if (more)
            page.RemoveAt(page.Count - 1);

        var items = page.Select(v =>
        {
            // Character i of both strings belongs to the vote with ordinal i.
            var ballot = country.Ballots[v.Ordinal];
            var outcome = ScoringRules.FromCode(country.Outcomes[v.Ordinal]);
            return new VoteContribution(v.Ordinal, v.AssemblyVoteId, v.Session, v.VoteDate, v.Title,
                v.DesiredOutcome == "Y" ? "For" : "Against", v.Weight!.Value,
                ballot is 'Y' or 'N' or 'A' or 'X' ? ballot.ToString() : null,
                outcome.ToString(), ScoringRules.Counts(outcome), outcome == VoteOutcome.Matched);
        }).ToList();

        var ranking = Ranking(country.CountryCode, country.CountryName, country.Score, country.Rank, ranked, country.CountedWeight,
            country.MatchedWeight, new OutcomeCounts(country.MatchedVotes, country.NotMatchedVotes, country.AbstainedVotes,
                country.AbsentVotes, country.NotAMemberVotes, country.NoBallotVotes));
        return new CountryBreakdown(ranking, country.Ballots.Length, items, more ? items[^1].Ordinal : null);
    }

    public async Task<SnapshotVotePage> VotesAsync(long id, bool? included, long afterVoteId, int take, CancellationToken ct)
    {
        var query = db.SnapshotVotes.AsNoTracking().Where(v => v.SnapshotId == id);
        if (included is not null)
            query = query.Where(v => v.Included == included);
        var total = await query.CountAsync(ct);
        var items = await query.Where(v => v.VoteId > afterVoteId).OrderBy(v => v.VoteId).Take(take + 1)
            .Select(v => new SnapshotVoteDto(v.VoteId, v.Ordinal, v.AssemblyVoteId, v.Session, v.VoteDate, v.Title,
                v.DesiredOutcome == null ? null : v.DesiredOutcome == "Y" ? "For" : "Against", v.Weight, v.Included, v.Reason))
            .ToListAsync(ct);
        var more = items.Count > take;
        if (more)
            items.RemoveAt(items.Count - 1);
        return new SnapshotVotePage(items, total, more ? items[^1].VoteId : null);
    }

    public Task<bool> ExistsAsync(long id, CancellationToken ct) => db.Snapshots.AnyAsync(s => s.Id == id, ct);

    private static CountryRanking Ranking(string code, string name, decimal? score, int? rank, int of, int counted, int matched,
        OutcomeCounts votes) =>
        new(code, name, score, ScoringRules.ScoreText(score), rank, ScoringRules.RankText(rank, of), counted, matched, votes);
}
