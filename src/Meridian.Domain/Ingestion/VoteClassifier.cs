namespace Meridian.Domain.Ingestion;

/// <summary>ING-06: every row gets exactly one of these.</summary>
public enum RowClassification { New, Unchanged, Duplicate, Invalid, Contradiction, Conflict }

/// <summary>What a Conflict row (or vote-field change) does to the review queue (DECISIONS §5, ING-12).</summary>
public enum ConflictAction
{
    /// <summary>A new pending conflict is opened.</summary>
    Open,

    /// <summary>The same proposal is already waiting for a decision; no second conflict is opened.</summary>
    AlreadyPending,

    /// <summary>The same proposal was already rejected from a file with the same sha256; nothing is opened.</summary>
    AlreadyRejected,
}

/// <summary>Vote fields that can be in conflict; the values match conflicts.field_name.</summary>
public static class VoteFields
{
    public const string Session = "session";
    public const string VoteDate = "vote_date";
    public const string Title = "title";
}

/// <summary>Target and proposed value of a conflict; mirrors the partial unique index on pending conflicts.</summary>
public sealed record ConflictKey(string Kind, string Target, string ProposedValue)
{
    public static ConflictKey ForCountry(string countryCode, Ballot proposed) => new("CountryVote", countryCode, proposed.ToString());
    public static ConflictKey ForField(string field, string proposed) => new("VoteField", field, proposed);
}

/// <summary>The approved state of a vote that already exists, plus its open and rejected proposals.</summary>
public sealed record ExistingVote(
    VoteDetails Details,
    IReadOnlyDictionary<string, Ballot> Ballots,
    IReadOnlySet<ConflictKey> Pending,
    IReadOnlySet<ConflictKey> RejectedFromSameFile);

public sealed record RowOutcome(
    int LineNumber,
    RowClassification Classification,
    string? Reason,
    string? CountryCode = null,
    Ballot? Ballot = null,
    ConflictAction? Action = null);

public sealed record VoteFieldChange(string Field, string ApprovedValue, string ProposedValue, ConflictAction Action);

public enum VoteStatus
{
    /// <summary>
    /// The vote does not exist yet and is created with <see cref="VoteClassification.Details"/>, even when none of its
    /// country rows is ingested (spec 7.2, votes 103 and 104). Its valid rows are New.
    /// </summary>
    New,

    /// <summary>The vote exists; rows and details are compared with the approved data.</summary>
    Existing,

    /// <summary>Valid rows disagree on session, date or title; nothing of this vote is ingested.</summary>
    Contradiction,

    /// <summary>
    /// There is no valid row and no structurally valid row to take the vote details from, or only rejected rows with
    /// details that disagree; no vote is created or compared.
    /// </summary>
    NoValidDetails,
}

public sealed record VoteClassification(
    string AssemblyVoteId,
    VoteStatus Status,
    VoteDetails? Details,
    IReadOnlyList<VoteFieldChange> FieldConflicts,
    string? ContradictionReason,
    IReadOnlyList<RowOutcome> Rows);

/// <summary>
/// Classifies all rows of one vote from one file (grouping key: assembly_vote_id; inside a vote: country_code)
/// against the approved data. Pure: the caller loads <see cref="ExistingVote"/> and applies the outcome.
/// </summary>
public static class VoteClassifier
{
    private const int MaxLinesListed = 10;

    public static VoteClassification Classify(string assemblyVoteId, IReadOnlyList<ValidatedRow> rows, ExistingVote? existing)
    {
        var outcomes = new List<RowOutcome>();

        // Invalid rows are always Invalid and never take part in the country checks.
        foreach (var invalid in rows.OfType<InvalidRow>())
            outcomes.Add(new(invalid.LineNumber, RowClassification.Invalid, invalid.Reason, invalid.CountryCode));

        var valid = rows.OfType<ValidRow>().OrderBy(r => r.LineNumber).ToList();

        // Where the vote details come from (DECISIONS §1, two tiers):
        // 1. If the vote has valid rows, only they count. An Invalid row never takes part in the agreement check, so a
        //    rejected row (for example a line mis-split by an unquoted comma in the title) never causes a Contradiction
        //    and never opens a vote-field conflict (ING-07).
        // 2. Without any valid row, the structurally valid Invalid rows (only an unknown or non-member country) give the
        //    details, so the vote can still be created (spec 7.2 vote 104). They only create votes; they never open
        //    vote-field conflicts.
        var fromValidRows = valid.Count > 0;
        var contributions = (fromValidRows
                ? valid.Select(r => (r.LineNumber, r.Details))
                : rows.OfType<InvalidRow>().Where(r => r.Details is not null).Select(r => (r.LineNumber, Details: r.Details!)))
            .OrderBy(c => c.LineNumber)
            .ToList();
        if (contributions.Count == 0)
            return Result(VoteStatus.NoValidDetails, null, [], null);

        // ING-05: one set of details per vote. If they disagree the system does not choose, so no row of this vote is
        // ingested (our interpretation, DECISIONS §1).
        var variants = contributions.GroupBy(c => c.Details, c => c.LineNumber).ToList();
        if (variants.Count > 1)
        {
            var reason = DescribeDetailVariants(variants);
            if (!fromValidRows)
                // Only rejected rows, and they disagree: nothing to choose from, so no vote is created.
                return Result(VoteStatus.NoValidDetails, null, [], "rejected rows only, and " + reason);
            outcomes.AddRange(valid.Select(r => new RowOutcome(r.LineNumber, RowClassification.Contradiction, reason, r.CountryCode, r.Ballot)));
            return Result(VoteStatus.Contradiction, null, [], reason);
        }

        var details = variants[0].Key;
        foreach (var country in valid.GroupBy(r => r.CountryCode))
        {
            var countryRows = country.ToList();
            if (countryRows.Select(r => r.Ballot).Distinct().Count() > 1)
            {
                // ING-05: one value per country per vote. All rows, including identical copies, are Contradiction.
                var reason = $"{country.Key} appears with different ballots: " +
                             string.Join(", ", countryRows.Select(r => $"{r.Ballot} (line {r.LineNumber})"));
                outcomes.AddRange(countryRows.Select(r => new RowOutcome(r.LineNumber, RowClassification.Contradiction, reason, r.CountryCode, r.Ballot)));
                continue;
            }

            // Identical rows: the first is classified, the copies are Duplicate. Names are ignored (ING-11).
            var first = countryRows[0];
            outcomes.Add(CompareWithApproved(first, existing));
            outcomes.AddRange(countryRows.Skip(1).Select(r => new RowOutcome(
                r.LineNumber, RowClassification.Duplicate, $"identical to line {first.LineNumber}", r.CountryCode, r.Ballot)));
        }

        if (existing is null)
            return Result(VoteStatus.New, details, [], null);

        // Only valid rows can open a vote-field conflict. Trade-off: a title change that only Invalid rows carry is not
        // reported as a conflict (DECISIONS §1).
        return Result(VoteStatus.Existing, details, fromValidRows ? CompareDetails(existing, details) : [], null);

        VoteClassification Result(VoteStatus status, VoteDetails? d, IReadOnlyList<VoteFieldChange> fieldConflicts, string? contradiction) =>
            new(assemblyVoteId, status, d, fieldConflicts, contradiction, outcomes.OrderBy(o => o.LineNumber).ToList());
    }

    private static RowOutcome CompareWithApproved(ValidRow row, ExistingVote? existing)
    {
        if (existing is null)
            return new(row.LineNumber, RowClassification.New, "new vote", row.CountryCode, row.Ballot);

        if (!existing.Ballots.TryGetValue(row.CountryCode, out var approved))
            return new(row.LineNumber, RowClassification.New, "country missing from the approved vote is added (ING-10)", row.CountryCode, row.Ballot);

        if (approved == row.Ballot)
            return new(row.LineNumber, RowClassification.Unchanged, null, row.CountryCode, row.Ballot);

        var action = ActionFor(ConflictKey.ForCountry(row.CountryCode, row.Ballot), existing);
        return new(row.LineNumber, RowClassification.Conflict, $"approved {approved}, file says {row.Ballot}", row.CountryCode, row.Ballot, action);
    }

    private static List<VoteFieldChange> CompareDetails(ExistingVote existing, VoteDetails proposed)
    {
        var changes = new List<VoteFieldChange>();
        Compare(VoteFields.Session, existing.Details.Session.ToString(), proposed.Session.ToString());
        Compare(VoteFields.VoteDate, Iso(existing.Details.VoteDate), Iso(proposed.VoteDate));
        Compare(VoteFields.Title, existing.Details.Title, proposed.Title);
        return changes;

        void Compare(string field, string approved, string value)
        {
            if (approved != value)
                changes.Add(new(field, approved, value, ActionFor(ConflictKey.ForField(field, value), existing)));
        }
    }

    private static ConflictAction ActionFor(ConflictKey key, ExistingVote existing) =>
        existing.Pending.Contains(key) ? ConflictAction.AlreadyPending
        : existing.RejectedFromSameFile.Contains(key) ? ConflictAction.AlreadyRejected
        : ConflictAction.Open;

    /// <summary>For example: "vote details differ between rows: title 'X' on 147 rows (lines 3446, ... and 137 more); title 'X (draft)' on 1 row (line 3593)".</summary>
    private static string DescribeDetailVariants(List<IGrouping<VoteDetails, int>> variants)
    {
        var all = variants.Select(v => v.Key).ToList();
        var differs = new List<(string Name, Func<VoteDetails, string> Value)>();
        if (all.Select(d => d.Session).Distinct().Count() > 1) differs.Add((ExportColumns.Session, d => d.Session.ToString()));
        if (all.Select(d => d.VoteDate).Distinct().Count() > 1) differs.Add((ExportColumns.VoteDate, d => Iso(d.VoteDate)));
        if (all.Select(d => d.Title).Distinct().Count() > 1) differs.Add((ExportColumns.Title, d => $"'{d.Title}'"));

        var parts = variants
            .OrderByDescending(v => v.Count()).ThenBy(v => v.First())
            .Select(v =>
            {
                var values = string.Join(", ", differs.Select(f => $"{f.Name} {f.Value(v.Key)}"));
                var lines = v.ToList();
                var listed = string.Join(", ", lines.Take(MaxLinesListed));
                var more = lines.Count > MaxLinesListed ? $" and {lines.Count - MaxLinesListed} more" : "";
                return $"{values} on {lines.Count} row{(lines.Count == 1 ? "" : "s")} (line{(lines.Count == 1 ? "" : "s")} {listed}{more})";
            });

        return "vote details differ between rows: " + string.Join("; ", parts);
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd");
}
