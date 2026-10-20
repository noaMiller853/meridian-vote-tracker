namespace Meridian.Domain.Snapshots;

/// <summary>
/// SNP-01: votes by filter (date range or session) and/or by list. Filter and list are a union: the votes that match
/// the filter plus the listed ones (DECISIONS §6).
/// </summary>
public sealed record SnapshotSelection(DateOnly? DateFrom, DateOnly? DateTo, int? Session, IReadOnlyList<string> AssemblyVoteIds)
{
    public const int MaxNameLength = 200;
    public const int MaxListedVotes = 10_000;

    public static class Fields
    {
        public const string Name = "name";
        public const string DateFrom = "dateFrom";
        public const string DateTo = "dateTo";
        public const string Session = "session";
        public const string AssemblyVoteIds = "assemblyVoteIds";
    }

    public bool HasFilter => DateFrom is not null || DateTo is not null || Session is not null;

    /// <summary>Every problem of the request, each under its field. Unknown vote ids are checked against the database.</summary>
    public IReadOnlyList<FieldError> Validate(string? name)
    {
        var errors = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(name))
            errors.Add(new(Fields.Name, "The name is required."));
        else if (name.Trim().Length > MaxNameLength)
            errors.Add(new(Fields.Name, $"The name is longer than {MaxNameLength} characters."));

        if (!HasFilter && AssemblyVoteIds.Count == 0)
            errors.Add(new(Fields.AssemblyVoteIds, "Select votes: a date range, a session, a list of vote ids, or a filter and a list."));
        if (DateFrom is not null && DateTo is not null && DateFrom > DateTo)
            errors.Add(new(Fields.DateFrom, $"dateFrom {DateFrom:yyyy-MM-dd} is after dateTo {DateTo:yyyy-MM-dd}."));
        if (Session is <= 0)
            errors.Add(new(Fields.Session, $"The session must be a positive whole number; got {Session}."));
        if (AssemblyVoteIds.Count > MaxListedVotes)
            errors.Add(new(Fields.AssemblyVoteIds, $"At most {MaxListedVotes} listed votes; use a filter for more."));
        if (AssemblyVoteIds.Any(string.IsNullOrWhiteSpace))
            errors.Add(new(Fields.AssemblyVoteIds, "A listed vote id is empty."));
        return errors;
    }

    /// <summary>The listed ids, trimmed and without repeats.</summary>
    public IReadOnlyList<string> DistinctIds() => AssemblyVoteIds.Select(i => i.Trim()).Distinct().ToList();
}

/// <summary>SNP-05: one published snapshot per quarter. A snapshot's quarter is the one of its latest included vote.</summary>
public static class Quarter
{
    public static string Of(DateOnly date) => $"{date.Year}-Q{(date.Month - 1) / 3 + 1}";
}
