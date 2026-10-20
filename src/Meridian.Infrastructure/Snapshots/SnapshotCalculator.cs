using System.Text.Json;
using Meridian.Domain.Snapshots;
using Meridian.Infrastructure.Imports;
using Meridian.Infrastructure.Persistence.Entities;
using Npgsql;
using NpgsqlTypes;

namespace Meridian.Infrastructure.Snapshots;

/// <summary>
/// SNP-02..SNP-04: freezes the selected votes and calculates every country's score inside the worker's work
/// transaction (REPEATABLE READ, so it sees the approved data as of one moment). Memory stays small: PostgreSQL builds
/// one row per country with that country's ballots as a string, and the scores are calculated as the rows arrive.
/// </summary>
internal sealed class SnapshotCalculator(NpgsqlConnection connection, NpgsqlTransaction transaction, int timeoutSeconds)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string NoPositionReason = "No position: excluded as confirmed by the creator (POS-04)";
    private const int MaxVotesListed = 20;

    public async Task<SnapshotResult> RunAsync(SnapshotJob job, IJobProgress progress, CancellationToken ct)
    {
        var selection = JsonSerializer.Deserialize<StoredSelection>(job.SelectionJson, Json)!;

        await progress.ReportAsync(BackgroundJob.PhaseSelecting, 0, null, force: true, ct);
        var (included, excluded, latest) = await FreezeVotesAsync(job, selection, ct);

        var votes = await FrozenVotesAsync(job.SnapshotId, ct);
        var countries = await ScalarAsync<long>("SELECT count(*) FROM countries", ct);
        await progress.ReportAsync(BackgroundJob.PhaseCalculating, 0, (int)countries, force: true, ct);

        var scores = new List<(string Code, string Name, string Ballots, CountryScore Score)>();
        var done = 0;
        await using (var cmd = Command(CountryBallotsSql))
        {
            cmd.Parameters.Add(new() { Value = job.SnapshotId });
            cmd.Parameters.Add(new() { Value = ScoringRules.NotAMember.ToString() });
            cmd.Parameters.Add(new() { Value = ScoringRules.NoBallot.ToString() });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var ballots = reader.GetString(2);
                if (ScoringRules.IsListed(ballots))
                    scores.Add((reader.GetString(0), reader.GetString(1), ballots, ScoringRules.Score(ballots, votes)));
                await progress.ReportAsync(BackgroundJob.PhaseCalculating, ++done, (int)countries, ct: ct);
            }
        }

        var ranks = ScoringRules.Rank(scores.Select(s => new RankInput(s.Code, s.Name, s.Score.CountedWeight, s.Score.MatchedWeight)))
            .ToDictionary(r => r.CountryCode);

        await progress.ReportAsync(BackgroundJob.PhaseWriting, 0, scores.Count, force: true, ct);
        await WriteScoresAsync(job.SnapshotId, scores, ranks, ct);

        var quarter = Quarter.Of(latest);
        await using (var saved = Command("UPDATE snapshots SET status = 'Saved', quarter = $2 WHERE id = $1"))
        {
            saved.Parameters.Add(new() { Value = job.SnapshotId });
            saved.Parameters.Add(new() { Value = quarter });
            await saved.ExecuteNonQueryAsync(ct);
        }
        await progress.ReportAsync(BackgroundJob.PhaseWriting, scores.Count, scores.Count, force: true, ct);

        return new SnapshotResult(included, excluded, scores.Count, ranks.Values.Count(r => r.Rank is not null), quarter);
    }

    /// <summary>
    /// SNP-01 and SNP-04: the selected votes (filter or list, as a union) with their details, position and weight as
    /// they are now. Included votes get ordinals 0..n-1 by date; the place of their character in every string.
    /// </summary>
    private async Task<(int Included, int Excluded, DateOnly Latest)> FreezeVotesAsync(SnapshotJob job, StoredSelection selection, CancellationToken ct)
    {
        await using (var freeze = Command("""
            INSERT INTO snapshot_votes (snapshot_id, vote_id, ordinal, assembly_vote_id, session, vote_date, title,
                                        desired_outcome, weight, included, reason)
            SELECT $1, v.id,
                   CASE WHEN p.vote_id IS NOT NULL
                        THEN (row_number() OVER (PARTITION BY p.vote_id IS NOT NULL ORDER BY v.vote_date, v.assembly_vote_id) - 1)::int
                   END,
                   v.assembly_vote_id, v.session, v.vote_date, v.title, p.desired_outcome, p.weight,
                   p.vote_id IS NOT NULL,
                   CASE WHEN p.vote_id IS NULL THEN $7 END
            FROM votes v
            LEFT JOIN vote_positions p ON p.vote_id = v.id
            WHERE ($2 AND ($3::date IS NULL OR v.vote_date >= $3)
                      AND ($4::date IS NULL OR v.vote_date <= $4)
                      AND ($5::int IS NULL OR v.session = $5))
               OR v.assembly_vote_id = ANY($6)
            """))
        {
            var hasFilter = selection.DateFrom is not null || selection.DateTo is not null || selection.Session is not null;
            freeze.Parameters.Add(new() { Value = job.SnapshotId });
            freeze.Parameters.Add(new() { Value = hasFilter });
            freeze.Parameters.Add(new() { Value = (object?)selection.DateFrom ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
            freeze.Parameters.Add(new() { Value = (object?)selection.DateTo ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
            freeze.Parameters.Add(new() { Value = (object?)selection.Session ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
            freeze.Parameters.Add(new() { Value = selection.AssemblyVoteIds.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            freeze.Parameters.Add(new() { Value = NoPositionReason });
            await freeze.ExecuteNonQueryAsync(ct);
        }

        await using var summary = Command("""
            SELECT count(*) FILTER (WHERE included)::int,
                   count(*) FILTER (WHERE NOT included)::int,
                   max(vote_date) FILTER (WHERE included),
                   (array_agg(assembly_vote_id ORDER BY vote_date, assembly_vote_id) FILTER (WHERE NOT included))[1:$2]
            FROM snapshot_votes WHERE snapshot_id = $1
            """);
        summary.Parameters.Add(new() { Value = job.SnapshotId });
        summary.Parameters.Add(new() { Value = MaxVotesListed });
        await using var reader = await summary.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var included = reader.GetInt32(0);
        var excluded = reader.GetInt32(1);
        var latest = reader.IsDBNull(2) ? (DateOnly?)null : reader.GetFieldValue<DateOnly>(2);
        var withoutPosition = reader.IsDBNull(3) ? [] : reader.GetFieldValue<string[]>(3);

        if (included + excluded == 0)
            throw new SnapshotRejectedException("The selection matches no vote.");
        // POS-04: checked when the snapshot was requested; votes created by an import in between are caught here.
        if (excluded > 0 && !job.ExcludeWithoutPosition)
            throw new SnapshotRejectedException(
                $"{excluded} selected vote(s) have no position, and leaving them out was not confirmed: " +
                $"{string.Join(", ", withoutPosition)}{(excluded > withoutPosition.Length ? ", …" : "")}. " +
                "Set their positions, or request the snapshot again with excludeVotesWithoutPosition.");
        if (included == 0)
            throw new SnapshotRejectedException($"None of the {excluded} selected vote(s) has a position, so there is nothing to score.");

        return (included, excluded, latest!.Value);
    }

    private async Task<List<FrozenVote>> FrozenVotesAsync(long snapshotId, CancellationToken ct)
    {
        var votes = new List<FrozenVote>();
        await using var cmd = Command(
            "SELECT ordinal, desired_outcome, weight FROM snapshot_votes WHERE snapshot_id = $1 AND included ORDER BY ordinal");
        cmd.Parameters.Add(new() { Value = snapshotId });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            votes.Add(new FrozenVote(reader.GetInt32(0), reader.GetString(1)[0], reader.GetInt16(2)));
        return votes;
    }

    /// <summary>
    /// One row per country: one character per included vote, in ordinal order. The ballot when the country was a member
    /// on the vote date (from the frozen date) and has one; '-' for a member without a ballot; '.' for a non-member.
    /// Membership periods of one country never overlap, so the join finds at most one.
    /// </summary>
    private const string CountryBallotsSql = """
        SELECT c.country_code, c.name,
               string_agg(CASE WHEN m.country_code IS NULL THEN $2
                               ELSE COALESCE(cv.ballot, $3) END, '' ORDER BY sv.ordinal)
        FROM countries c
        CROSS JOIN snapshot_votes sv
        LEFT JOIN country_memberships m
               ON m.country_code = c.country_code
              AND m.member_from <= sv.vote_date
              AND (m.member_to IS NULL OR sv.vote_date <= m.member_to)
        LEFT JOIN country_votes cv
               ON cv.vote_id = sv.vote_id AND cv.country_code = c.country_code
        WHERE sv.snapshot_id = $1 AND sv.included
        GROUP BY c.country_code, c.name
        ORDER BY c.country_code
        """;

    private async Task WriteScoresAsync(long snapshotId, List<(string Code, string Name, string Ballots, CountryScore Score)> scores,
        Dictionary<string, RankedCountry> ranks, CancellationToken ct)
    {
        await using var copy = await connection.BeginBinaryImportAsync("""
            COPY snapshot_scores (snapshot_id, country_code, country_name, ballots, outcomes, score, rank, counted_weight,
                                  matched_weight, matched_votes, not_matched_votes, abstained_votes, absent_votes,
                                  not_a_member_votes, no_ballot_votes) FROM STDIN (FORMAT BINARY)
            """, ct);
        copy.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        foreach (var (code, name, ballots, score) in scores)
        {
            await copy.StartRowAsync(ct);
            await copy.WriteAsync(snapshotId, NpgsqlDbType.Bigint, ct);
            await copy.WriteAsync(code, NpgsqlDbType.Text, ct);
            await copy.WriteAsync(name, NpgsqlDbType.Text, ct);
            await copy.WriteAsync(ballots, NpgsqlDbType.Text, ct);
            await copy.WriteAsync(score.Outcomes, NpgsqlDbType.Text, ct);
            if (score.Score is { } value) await copy.WriteAsync(value, NpgsqlDbType.Numeric, ct); else await copy.WriteNullAsync(ct);
            if (ranks[code].Rank is { } rank) await copy.WriteAsync(rank, NpgsqlDbType.Integer, ct); else await copy.WriteNullAsync(ct);
            await copy.WriteAsync(score.CountedWeight, NpgsqlDbType.Integer, ct);
            await copy.WriteAsync(score.MatchedWeight, NpgsqlDbType.Integer, ct);
            foreach (var outcome in new[] { VoteOutcome.Matched, VoteOutcome.NotMatched, VoteOutcome.Abstained, VoteOutcome.Absent,
                                            VoteOutcome.NotAMember, VoteOutcome.NoBallot })
                await copy.WriteAsync(score.Counts[outcome], NpgsqlDbType.Integer, ct);
        }
        await copy.CompleteAsync(ct);
    }

    private NpgsqlCommand Command(string sql) => new(sql, connection, transaction) { CommandTimeout = timeoutSeconds };

    private async Task<T> ScalarAsync<T>(string sql, CancellationToken ct)
    {
        await using var cmd = Command(sql);
        return (T)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
