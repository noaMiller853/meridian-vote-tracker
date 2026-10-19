using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meridian.Domain.Ingestion;
using Meridian.Domain.Membership;
using Meridian.Infrastructure.Startup;
using Npgsql;
using NpgsqlTypes;

namespace Meridian.Infrastructure.Imports;

/// <summary>
/// State of one import inside its work transaction: reference data, the per-batch writes and the report counters.
/// Every command runs on the work connection and transaction, with the configured command timeout.
/// </summary>
internal sealed class ImportRun(ImportJob job, NpgsqlConnection connection, NpgsqlTransaction transaction, int timeoutSeconds)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private const int MaxPairsPerField = 50;
    private const int MaxVotesListed = 100;

    private long _systemUserId;
    private MembershipCalendar _calendar = null!;

    // Report counters (ING-14).
    private readonly Dictionary<RowClassification, int> _counts = Enum.GetValues<RowClassification>().ToDictionary(c => c, _ => 0);
    private int _normalizedRows;
    private readonly Dictionary<string, Dictionary<(string Original, string Normalized), int>> _normalizations = [];
    private readonly Dictionary<string, int> _normalizedRowsByField = [];
    private readonly Dictionary<string, int> _emptyOptional = new() { [ExportColumns.CountryName] = 0, [ExportColumns.PublishedAt] = 0 };
    private readonly Dictionary<VoteStatus, List<string>> _votesByStatus = Enum.GetValues<VoteStatus>().ToDictionary(s => s, _ => new List<string>());
    private int _votesCreated;
    private readonly Dictionary<(string Kind, ConflictAction Action), int> _conflicts = [];
    private readonly HashSet<string> _publishedAt = [];

    public ImportJob Job { get; } = job;
    public NpgsqlConnection Connection { get; } = connection;
    public TimeSpan Timeout { get; } = TimeSpan.FromSeconds(timeoutSeconds);

    public NpgsqlCommand Command(string sql) => new(sql, Connection, transaction) { CommandTimeout = timeoutSeconds };

    public async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var cmd = Command(sql);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task LoadReferenceDataAsync(CancellationToken ct)
    {
        await using (var cmd = Command("SELECT id FROM users WHERE name = $1"))
        {
            cmd.Parameters.Add(new() { Value = DatabaseInitializer.SystemUserName });
            _systemUserId = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        // Loaded once per import, not per row (DECISIONS §2).
        var periods = new List<MembershipPeriod>();
        await using (var cmd = Command("SELECT country_code, member_from, member_to FROM country_memberships"))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                periods.Add(new MembershipPeriod(reader.GetString(0), reader.GetFieldValue<DateOnly>(1),
                    reader.IsDBNull(2) ? null : reader.GetFieldValue<DateOnly>(2)));
        _calendar = new MembershipCalendar(periods);
    }

    // ------------------------------------------------------------------ one batch of complete votes

    public async Task ProcessBatchAsync(IReadOnlyList<IGrouping<string?, StagedLine>> groups, CancellationToken ct)
    {
        var rowWrites = new List<RowWrite>();

        // Rows without a vote id cannot be grouped; they are Invalid on their own.
        foreach (var line in groups.Where(g => string.IsNullOrWhiteSpace(g.Key)).SelectMany(g => g))
        {
            var validated = RowValidator.Validate(line.Row, _calendar);
            var reason = validated is InvalidRow invalid ? invalid.Reason : "assembly_vote_id is empty";
            rowWrites.Add(new RowWrite(line, validated, new RowOutcome(line.Row.LineNumber, RowClassification.Invalid, reason)));
        }

        var voteGroups = groups.Where(g => !string.IsNullOrWhiteSpace(g.Key)).ToList();
        var approved = await LoadApprovedAsync(voteGroups.Select(g => g.Key!).ToArray(), ct);

        var plans = new List<VotePlan>();
        foreach (var group in voteGroups)
        {
            var lines = group.ToList();
            var validated = lines.Select(l => RowValidator.Validate(l.Row, _calendar)).ToList();
            approved.TryGetValue(group.Key!, out var existing);
            var result = VoteClassifier.Classify(group.Key!, validated, existing?.ToDomain());

            var byLine = lines.Zip(validated).ToDictionary(x => x.First.Row.LineNumber);
            rowWrites.AddRange(result.Rows.Select(o => new RowWrite(byLine[o.LineNumber].First, byLine[o.LineNumber].Second, o)));
            plans.Add(new VotePlan(group.Key!, result, existing, validated));
        }

        // FK order: import_rows first (source of ballots and conflicts), then votes, ballots, conflicts.
        await CopyImportRowsAsync(rowWrites, ct);
        var voteIds = await InsertVotesAsync(plans, ct);
        foreach (var plan in plans)
            plan.VoteId = plan.Existing?.Id ?? (voteIds.TryGetValue(plan.Key, out var id) ? id : null);

        await InsertBallotsAsync(plans, ct);
        var opened = await InsertConflictsAsync(plans, ct);
        await CopyImportVotesAsync(plans, opened, ct);
        await CopyAuditAsync(plans, opened, ct);

        Count(rowWrites, plans, opened);
    }

    /// <summary>The batch query: approved data of the votes in this batch, in one round trip (five result sets).</summary>
    private async Task<Dictionary<string, ApprovedVote>> LoadApprovedAsync(string[] voteIds, CancellationToken ct)
    {
        var result = new Dictionary<string, ApprovedVote>();
        if (voteIds.Length == 0)
            return result;

        await using var batch = new NpgsqlBatch(Connection, transaction) { Timeout = timeoutSeconds };
        batch.BatchCommands.Add(Batch("""
            SELECT v.id, v.assembly_vote_id, v.session, v.vote_date, v.title,
                   v.session_source_import_id, v.vote_date_source_import_id, v.title_source_import_id
            FROM votes v
            WHERE v.assembly_vote_id = ANY($1)
            """, voteIds));
        batch.BatchCommands.Add(Batch("""
            SELECT v.assembly_vote_id, cv.country_code, cv.ballot, cv.source_import_id
            FROM country_votes cv
            JOIN votes v ON v.id = cv.vote_id
            WHERE v.assembly_vote_id = ANY($1)
            """, voteIds));
        batch.BatchCommands.Add(Batch("""
            SELECT v.assembly_vote_id, c.kind, COALESCE(c.country_code, c.field_name) AS target, c.proposed_value
            FROM conflicts c
            JOIN votes v ON v.id = c.vote_id
            WHERE v.assembly_vote_id = ANY($1) AND c.status = 'Pending'
            """, voteIds));
        var rejected = Batch("""
            SELECT v.assembly_vote_id, c.kind, COALESCE(c.country_code, c.field_name) AS target, c.proposed_value
            FROM conflicts c
            JOIN votes v   ON v.id = c.vote_id
            JOIN imports i ON i.id = c.proposed_source_import_id
            WHERE v.assembly_vote_id = ANY($1) AND c.status = 'Rejected'
              AND i.kind = 'Votes' AND i.sha256 = $2
            """, voteIds);
        rejected.Parameters.Add(new() { Value = Job.Sha256 });
        batch.BatchCommands.Add(rejected);
        // Values an approval replaced that came from a file with the same sha256 (AlreadySuperseded, ING-12).
        var superseded = Batch("""
            SELECT v.assembly_vote_id, c.kind, COALESCE(c.country_code, c.field_name) AS target, c.replaced_value
            FROM conflicts c
            JOIN votes v   ON v.id = c.vote_id
            JOIN imports i ON i.id = c.replaced_source_import_id
            WHERE v.assembly_vote_id = ANY($1) AND c.status = 'Approved'
              AND i.kind = 'Votes' AND i.sha256 = $2
            """, voteIds);
        superseded.Parameters.Add(new() { Value = Job.Sha256 });
        batch.BatchCommands.Add(superseded);

        await using var reader = await batch.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(1)] = new ApprovedVote(
                reader.GetInt64(0),
                new VoteDetails(reader.GetInt32(2), reader.GetFieldValue<DateOnly>(3), reader.GetString(4)),
                new Dictionary<string, long>
                {
                    [VoteFields.Session] = reader.GetInt64(5),
                    [VoteFields.VoteDate] = reader.GetInt64(6),
                    [VoteFields.Title] = reader.GetInt64(7),
                });

        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)].Ballots[reader.GetString(1)] = (Enum.Parse<Ballot>(reader.GetString(2)), reader.GetInt64(3));

        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)].Pending.Add(new ConflictKey(reader.GetString(1), reader.GetString(2), reader.GetString(3)));

        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)].Rejected.Add(new ConflictKey(reader.GetString(1), reader.GetString(2), reader.GetString(3)));

        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)].Superseded.Add(new ConflictKey(reader.GetString(1), reader.GetString(2), reader.GetString(3)));

        return result;

        static NpgsqlBatchCommand Batch(string sql, string[] ids) =>
            new(sql) { Parameters = { new() { Value = ids, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text } } };
    }

    private async Task CopyImportRowsAsync(List<RowWrite> rows, CancellationToken ct)
    {
        await using var copy = await Connection.BeginBinaryImportAsync("""
            COPY import_rows (import_id, row_number, raw_line, assembly_vote_id, session, vote_date, title, country_code,
                              original_country_code, country_name, ballot, published_at, normalized, classification, reason,
                              original_value, field_count, problems, normalizations, conflict_action) FROM STDIN (FORMAT BINARY)
            """, ct);
        copy.Timeout = Timeout;
        foreach (var w in rows)
        {
            var raw = w.Line.Row;
            var validated = w.Validated;
            await copy.StartRowAsync(ct);
            await copy.WriteAsync(Job.ImportId, NpgsqlDbType.Bigint, ct);
            await copy.WriteAsync(raw.LineNumber, NpgsqlDbType.Integer, ct);
            await copy.WriteAsync(w.Line.RawText, NpgsqlDbType.Text, ct);
            await Copy.Text(copy, raw.AssemblyVoteId, ct);
            await Copy.Text(copy, raw.Session, ct);
            await Copy.Text(copy, raw.VoteDate, ct);
            await Copy.Text(copy, raw.Title, ct);
            await Copy.Text(copy, validated switch { ValidRow v => v.CountryCode, InvalidRow i => i.CountryCode, _ => null }, ct);
            await Copy.Text(copy, raw.CountryCode, ct);
            await Copy.Text(copy, raw.CountryName, ct);
            await Copy.Text(copy, raw.Ballot, ct);
            await Copy.Text(copy, raw.PublishedAt, ct);
            await copy.WriteAsync(validated.Normalizations.Count > 0, NpgsqlDbType.Boolean, ct);
            await copy.WriteAsync(w.Outcome.Classification.ToString(), NpgsqlDbType.Text, ct);
            await Copy.Text(copy, w.Outcome.Reason, ct);
            await Copy.Text(copy, (validated as InvalidRow)?.Problems.FirstOrDefault()?.OriginalValue, ct);
            await copy.WriteAsync(raw.FieldCount, NpgsqlDbType.Integer, ct);
            await Copy.Json(copy, validated is InvalidRow invalid ? JsonSerializer.Serialize(invalid.Problems, Json) : null, ct);
            await Copy.Json(copy, validated.Normalizations.Count > 0 ? JsonSerializer.Serialize(validated.Normalizations, Json) : null, ct);
            await Copy.Text(copy, w.Outcome.Action?.ToString(), ct);
        }
        await copy.CompleteAsync(ct);
    }

    /// <summary>Creates the votes the classifier marked New (DECISIONS §1: even when none of their ballots is ingested).</summary>
    private async Task<Dictionary<string, long>> InsertVotesAsync(List<VotePlan> plans, CancellationToken ct)
    {
        var created = plans.Where(p => p.Result.Status == VoteStatus.New).ToList();
        var ids = new Dictionary<string, long>();
        if (created.Count == 0)
            return ids;

        await using var cmd = Command("""
            INSERT INTO votes (assembly_vote_id, session, vote_date, title, first_import_id,
                               session_source_import_id, vote_date_source_import_id, title_source_import_id, created_at)
            SELECT v, s, d, t, $5, $5, $5, $5, now()
            FROM unnest($1::text[], $2::int[], $3::date[], $4::text[]) AS x(v, s, d, t)
            ON CONFLICT (assembly_vote_id) DO NOTHING
            RETURNING id, assembly_vote_id
            """);
        cmd.Parameters.Add(new() { Value = created.Select(p => p.Key).ToArray() });
        cmd.Parameters.Add(new() { Value = created.Select(p => p.Result.Details!.Session).ToArray() });
        cmd.Parameters.Add(new() { Value = created.Select(p => p.Result.Details!.VoteDate).ToArray() });
        cmd.Parameters.Add(new() { Value = created.Select(p => p.Result.Details!.Title).ToArray() });
        cmd.Parameters.Add(new() { Value = Job.ImportId });
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                ids[reader.GetString(1)] = reader.GetInt64(0);

        // One worker classifies against what it just read in the same transaction, so a missing id is a bug.
        var missing = created.Where(p => !ids.ContainsKey(p.Key)).Select(p => p.Key).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Votes already existed although classified as new: {string.Join(", ", missing)}.");
        return ids;
    }

    private async Task InsertBallotsAsync(List<VotePlan> plans, CancellationToken ct)
    {
        var ballots = plans
            .SelectMany(p => p.Result.Rows.Where(r => r.Classification == RowClassification.New).Select(r => (p.VoteId!.Value, r)))
            .ToList();
        if (ballots.Count == 0)
            return;

        await using var cmd = Command("""
            INSERT INTO country_votes (vote_id, country_code, ballot, source_import_id, source_row_number, approved_by_id, approved_at)
            SELECT v, c, b, $5, r, $6, now()
            FROM unnest($1::bigint[], $2::text[], $3::text[], $4::int[]) AS x(v, c, b, r)
            ON CONFLICT (vote_id, country_code) DO NOTHING
            """);
        cmd.Parameters.Add(new() { Value = ballots.Select(b => b.Item1).ToArray() });
        cmd.Parameters.Add(new() { Value = ballots.Select(b => b.r.CountryCode!).ToArray() });
        cmd.Parameters.Add(new() { Value = ballots.Select(b => b.r.Ballot!.Value.ToString()).ToArray() });
        cmd.Parameters.Add(new() { Value = ballots.Select(b => b.r.LineNumber).ToArray() });
        cmd.Parameters.Add(new() { Value = Job.ImportId });
        cmd.Parameters.Add(new() { Value = _systemUserId }); // approved_by is for human decisions (DECISIONS §7)
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<List<OpenedConflict>> InsertConflictsAsync(List<VotePlan> plans, CancellationToken ct)
    {
        var rows = new List<(long Vote, string Kind, string? Country, string? Field, string Approved, string Proposed, long Source, int Line)>();
        foreach (var plan in plans.Where(p => p.Existing is not null))
        {
            foreach (var r in plan.Result.Rows.Where(r => r.Action == ConflictAction.Open))
            {
                var (approvedBallot, source) = plan.Existing!.Ballots[r.CountryCode!];
                rows.Add((plan.VoteId!.Value, "CountryVote", r.CountryCode, null, approvedBallot.ToString(), r.Ballot!.Value.ToString(), source, r.LineNumber));
            }
            foreach (var f in plan.Result.FieldConflicts.Where(f => f.Action == ConflictAction.Open))
                rows.Add((plan.VoteId!.Value, "VoteField", null, f.Field, f.ApprovedValue, f.ProposedValue, plan.Existing!.FieldSources[f.Field], plan.FirstDetailsLine));
        }

        var opened = new List<OpenedConflict>();
        if (rows.Count == 0)
            return opened;

        await using var cmd = Command("""
            INSERT INTO conflicts (vote_id, kind, country_code, field_name, approved_value, proposed_value,
                                   approved_source_import_id, proposed_source_import_id, proposed_source_row_number, status, created_at)
            SELECT v, k, c, f, a, p, s, $8, r, 'Pending', now()
            FROM unnest($1::bigint[], $2::text[], $3::text[], $4::text[], $5::text[], $6::text[], $7::bigint[], $9::int[])
                 AS x(v, k, c, f, a, p, s, r)
            ON CONFLICT (vote_id, kind, country_code, field_name, proposed_value) WHERE status = 'Pending' DO NOTHING
            RETURNING id, vote_id, kind, COALESCE(country_code, field_name), approved_value, proposed_value
            """);
        cmd.Parameters.Add(new() { Value = rows.Select(r => r.Vote).ToArray() });
        cmd.Parameters.Add(new() { Value = rows.Select(r => r.Kind).ToArray() });
        cmd.Parameters.Add(new() { Value = rows.Select(r => r.Country).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = rows.Select(r => r.Field).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = rows.Select(r => r.Approved).ToArray() });
        cmd.Parameters.Add(new() { Value = rows.Select(r => r.Proposed).ToArray() });
        cmd.Parameters.Add(new() { Value = rows.Select(r => r.Source).ToArray() });
        cmd.Parameters.Add(new() { Value = Job.ImportId });
        cmd.Parameters.Add(new() { Value = rows.Select(r => r.Line).ToArray() });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            opened.Add(new OpenedConflict(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5)));
        return opened;
    }

    private async Task CopyImportVotesAsync(List<VotePlan> plans, List<OpenedConflict> opened, CancellationToken ct)
    {
        var openedByVote = opened.GroupBy(o => o.VoteId).ToDictionary(g => g.Key, g => g.Count());
        await using var copy = await Connection.BeginBinaryImportAsync("""
            COPY import_votes (import_id, assembly_vote_id, vote_id, status, vote_created, row_count, new_count, unchanged_count,
                               duplicate_count, invalid_count, contradiction_count, conflict_count, conflicts_opened,
                               conflicts_already_pending, conflicts_already_rejected, conflicts_already_superseded,
                               field_conflicts, contradiction_reason)
            FROM STDIN (FORMAT BINARY)
            """, ct);
        copy.Timeout = Timeout;
        foreach (var plan in plans)
        {
            var rows = plan.Result.Rows;
            int Class(RowClassification c) => rows.Count(r => r.Classification == c);
            int Actions(ConflictAction a) => rows.Count(r => r.Action == a) + plan.Result.FieldConflicts.Count(f => f.Action == a);

            await copy.StartRowAsync(ct);
            await copy.WriteAsync(Job.ImportId, NpgsqlDbType.Bigint, ct);
            await copy.WriteAsync(plan.Key, NpgsqlDbType.Text, ct);
            await Copy.Long(copy, plan.VoteId, ct);
            await copy.WriteAsync(plan.Result.Status.ToString(), NpgsqlDbType.Text, ct);
            await copy.WriteAsync(plan.Result.Status == VoteStatus.New, NpgsqlDbType.Boolean, ct);
            await copy.WriteAsync(rows.Count, NpgsqlDbType.Integer, ct);
            foreach (var c in new[] { RowClassification.New, RowClassification.Unchanged, RowClassification.Duplicate,
                                      RowClassification.Invalid, RowClassification.Contradiction, RowClassification.Conflict })
                await copy.WriteAsync(Class(c), NpgsqlDbType.Integer, ct);
            await copy.WriteAsync(plan.VoteId is { } id ? openedByVote.GetValueOrDefault(id) : 0, NpgsqlDbType.Integer, ct);
            await copy.WriteAsync(Actions(ConflictAction.AlreadyPending), NpgsqlDbType.Integer, ct);
            await copy.WriteAsync(Actions(ConflictAction.AlreadyRejected), NpgsqlDbType.Integer, ct);
            await copy.WriteAsync(Actions(ConflictAction.AlreadySuperseded), NpgsqlDbType.Integer, ct);
            await Copy.Json(copy, plan.Result.FieldConflicts.Count > 0 ? JsonSerializer.Serialize(plan.Result.FieldConflicts, Json) : null, ct);
            await Copy.Text(copy, plan.Result.ContradictionReason, ct);
        }
        await copy.CompleteAsync(ct);
    }

    /// <summary>One audit row per vote and import (DECISIONS §7), plus one per conflict opened. Actor: system.</summary>
    private async Task CopyAuditAsync(List<VotePlan> plans, List<OpenedConflict> opened, CancellationToken ct)
    {
        var events = new List<(string Entity, string EntityId, long VoteId, string Action, string Value)>();
        foreach (var plan in plans.Where(p => p.VoteId is not null))
        {
            var id = plan.VoteId!.Value;
            if (plan.Result.Status == VoteStatus.New)
            {
                var d = plan.Result.Details!;
                events.Add(("vote", id.ToString(), id, "VoteCreated", JsonSerializer.Serialize(
                    new { assemblyVoteId = plan.Key, session = d.Session, voteDate = d.VoteDate, title = d.Title }, Json)));
            }
            var added = plan.Result.Rows.Where(r => r.Classification == RowClassification.New).ToList();
            if (added.Count > 0)
                events.Add(("vote", id.ToString(), id, "BallotsAdded", JsonSerializer.Serialize(
                    new { count = added.Count, ballots = added.ToDictionary(r => r.CountryCode!, r => r.Ballot!.Value.ToString()) }, Json)));
        }
        foreach (var c in opened)
            events.Add(("conflict", c.Id.ToString(), c.VoteId, "ConflictOpened", JsonSerializer.Serialize(
                new { kind = c.Kind, target = c.Target, approvedValue = c.Approved, proposedValue = c.Proposed }, Json)));
        if (events.Count == 0)
            return;

        await using var copy = await Connection.BeginBinaryImportAsync("""
            COPY audit_events (entity, entity_id, vote_id, action, new_value, actor_id, import_id, correlation_id, occurred_at)
            FROM STDIN (FORMAT BINARY)
            """, ct);
        copy.Timeout = Timeout;
        var now = DateTimeOffset.UtcNow;
        foreach (var e in events)
        {
            await copy.StartRowAsync(ct);
            await copy.WriteAsync(e.Entity, NpgsqlDbType.Text, ct);
            await copy.WriteAsync(e.EntityId, NpgsqlDbType.Text, ct);
            await copy.WriteAsync(e.VoteId, NpgsqlDbType.Bigint, ct);
            await copy.WriteAsync(e.Action, NpgsqlDbType.Text, ct);
            await copy.WriteAsync(e.Value, NpgsqlDbType.Jsonb, ct);
            await copy.WriteAsync(_systemUserId, NpgsqlDbType.Bigint, ct);
            await copy.WriteAsync(Job.ImportId, NpgsqlDbType.Bigint, ct);
            await Copy.Text(copy, Job.CorrelationId, ct);
            await copy.WriteAsync(now, NpgsqlDbType.TimestampTz, ct);
        }
        await copy.CompleteAsync(ct);
    }

    private void Count(List<RowWrite> rows, List<VotePlan> plans, List<OpenedConflict> opened)
    {
        foreach (var w in rows)
        {
            _counts[w.Outcome.Classification]++;
            var v = w.Validated;
            if (v.Normalizations.Count > 0)
                _normalizedRows++;
            foreach (var n in v.Normalizations)
            {
                var pairs = _normalizations.TryGetValue(n.Field, out var p) ? p : _normalizations[n.Field] = [];
                pairs[(n.Original, n.Normalized)] = pairs.GetValueOrDefault((n.Original, n.Normalized)) + 1;
                _normalizedRowsByField[n.Field] = _normalizedRowsByField.GetValueOrDefault(n.Field) + 1;
            }
            foreach (var field in v.EmptyOptionalFields)
                _emptyOptional[field]++;
            if (v is ValidRow { PublishedAt: { Length: > 0 } published })
                _publishedAt.Add(published);
        }

        foreach (var plan in plans)
        {
            _votesByStatus[plan.Result.Status].Add(plan.Key);
            if (plan.Result.Status == VoteStatus.New)
                _votesCreated++;
            foreach (var r in plan.Result.Rows.Where(r => r.Action is not null and not ConflictAction.Open))
                Add("CountryVote", r.Action!.Value);
            foreach (var f in plan.Result.FieldConflicts.Where(f => f.Action != ConflictAction.Open))
                Add("VoteField", f.Action);
        }
        foreach (var c in opened)
            Add(c.Kind, ConflictAction.Open);

        void Add(string kind, ConflictAction action) => _conflicts[(kind, action)] = _conflicts.GetValueOrDefault((kind, action)) + 1;
    }

    // ------------------------------------------------------------------ the end of the import

    /// <summary>Counters and the stored report (ING-14, ING-15). Per-row and per-vote detail stay in their tables.</summary>
    public async Task WriteSummaryAsync(int lines, CancellationToken ct)
    {
        var warnings = new List<string>();
        var publishedAt = PublishedAt(warnings);
        int Conflicts(ConflictAction action, string? kind = null) =>
            _conflicts.Where(c => c.Key.Action == action && (kind is null || c.Key.Kind == kind)).Sum(c => c.Value);

        var opened = Conflicts(ConflictAction.Open);
        var pending = Conflicts(ConflictAction.AlreadyPending);
        var rejected = Conflicts(ConflictAction.AlreadyRejected);
        var superseded = Conflicts(ConflictAction.AlreadySuperseded);

        var report = new
        {
            status = "Succeeded",
            lineNumbers = "line in the file (the header is line 1)",
            lines,
            counts = _counts.ToDictionary(c => c.Key.ToString(), c => c.Value),
            publishedAt,
            warnings,
            votes = new
            {
                inFile = _votesByStatus.Sum(v => v.Value.Count),
                created = _votesCreated,
                existing = _votesByStatus[VoteStatus.Existing].Count,
                contradiction = Listed(_votesByStatus[VoteStatus.Contradiction]),
                noValidDetails = Listed(_votesByStatus[VoteStatus.NoValidDetails]),
            },
            conflicts = new
            {
                summary = $"new conflicts opened: {opened} ({pending} already waiting for a decision, {rejected} already rejected, {superseded} already superseded)",
                opened,
                alreadyPending = pending,
                alreadyRejected = rejected,
                alreadySuperseded = superseded,
                ballot = new
                {
                    opened = Conflicts(ConflictAction.Open, "CountryVote"),
                    alreadyPending = Conflicts(ConflictAction.AlreadyPending, "CountryVote"),
                    alreadyRejected = Conflicts(ConflictAction.AlreadyRejected, "CountryVote"),
                    alreadySuperseded = Conflicts(ConflictAction.AlreadySuperseded, "CountryVote"),
                },
                voteField = new
                {
                    opened = Conflicts(ConflictAction.Open, "VoteField"),
                    alreadyPending = Conflicts(ConflictAction.AlreadyPending, "VoteField"),
                    alreadyRejected = Conflicts(ConflictAction.AlreadyRejected, "VoteField"),
                    alreadySuperseded = Conflicts(ConflictAction.AlreadySuperseded, "VoteField"),
                },
            },
            normalizations = new
            {
                rows = _normalizedRows,
                byField = _normalizations.OrderBy(f => f.Key).ToDictionary(f => f.Key, f => new
                {
                    rows = _normalizedRowsByField[f.Key],
                    values = f.Value.OrderByDescending(p => p.Value).ThenBy(p => p.Key.Original, StringComparer.Ordinal).Take(MaxPairsPerField)
                        .Select(p => new { original = p.Key.Original, normalized = p.Key.Normalized, rows = p.Value }),
                    moreValues = Math.Max(0, f.Value.Count - MaxPairsPerField),
                }),
            },
            emptyOptionalFields = _emptyOptional,
        };

        await using var cmd = Command("""
            UPDATE imports
            SET new_count = $2, unchanged_count = $3, duplicate_count = $4, invalid_count = $5, contradiction_count = $6,
                conflict_count = $7, normalized_count = $8, published_at = $9, report = $10::jsonb
            WHERE id = $1
            """);
        cmd.Parameters.Add(new() { Value = Job.ImportId });
        foreach (var c in new[] { RowClassification.New, RowClassification.Unchanged, RowClassification.Duplicate,
                                  RowClassification.Invalid, RowClassification.Contradiction, RowClassification.Conflict })
            cmd.Parameters.Add(new() { Value = _counts[c] });
        cmd.Parameters.Add(new() { Value = _normalizedRows });
        cmd.Parameters.Add(new() { Value = (object?)publishedAt ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new() { Value = JsonSerializer.Serialize(report, Json) });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public string CountsText() => string.Join(", ", _counts.Select(c => $"{c.Key} {c.Value}"));

    /// <summary>DECISIONS §7: the single value when all valid rows agree; otherwise the latest one and a warning.</summary>
    private DateTimeOffset? PublishedAt(List<string> warnings)
    {
        var parsed = new List<DateTimeOffset>();
        var unreadable = new List<string>();
        foreach (var value in _publishedAt)
        {
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var p))
                parsed.Add(p);
            else
                unreadable.Add(value);
        }
        if (unreadable.Count > 0)
            warnings.Add($"published_at could not be read on some rows ({string.Join(", ", unreadable.Take(5).Select(u => $"'{u}'"))}{(unreadable.Count > 5 ? ", …" : "")}); it is not used.");
        if (parsed.Count == 0)
            return null;

        var latest = parsed.Max();
        if (parsed.Count > 1)
            warnings.Add($"The rows have {parsed.Count} different published_at values (from {parsed.Min():yyyy-MM-ddTHH:mm:ssZ} to {latest:yyyy-MM-ddTHH:mm:ssZ}); the latest is stored as the file's publication time.");
        return latest;
    }

    private static object Listed(List<string> votes) => new
    {
        count = votes.Count,
        assemblyVoteIds = votes.Take(MaxVotesListed),
        more = Math.Max(0, votes.Count - MaxVotesListed),
    };

    // ------------------------------------------------------------------ types

    private sealed record RowWrite(StagedLine Line, ValidatedRow Validated, RowOutcome Outcome);

    private sealed record OpenedConflict(long Id, long VoteId, string Kind, string Target, string Approved, string Proposed);

    private sealed class VotePlan(string key, VoteClassification result, ApprovedVote? existing, List<ValidatedRow> validated)
    {
        public string Key { get; } = key;
        public VoteClassification Result { get; } = result;
        public ApprovedVote? Existing { get; } = existing;
        public long? VoteId { get; set; }

        /// <summary>Source row of a vote-field conflict: the first line that contributed the vote details.</summary>
        public int FirstDetailsLine { get; } = validated
            .Where(v => v is ValidRow || v is InvalidRow { Details: not null })
            .Select(v => v.LineNumber)
            .DefaultIfEmpty(0) // no details: the vote has no field conflicts either
            .Min();
    }

    private sealed class ApprovedVote(long id, VoteDetails details, Dictionary<string, long> fieldSources)
    {
        public long Id { get; } = id;

        /// <summary>The import that set each vote field (<see cref="VoteFields"/>): the approved source of a field conflict.</summary>
        public Dictionary<string, long> FieldSources { get; } = fieldSources;
        public Dictionary<string, (Ballot Ballot, long SourceImportId)> Ballots { get; } = [];
        public HashSet<ConflictKey> Pending { get; } = [];
        public HashSet<ConflictKey> Rejected { get; } = [];
        public HashSet<ConflictKey> Superseded { get; } = [];

        public ExistingVote ToDomain() =>
            new(details, Ballots.ToDictionary(b => b.Key, b => b.Value.Ballot), Pending, Rejected, Superseded);
    }
}
