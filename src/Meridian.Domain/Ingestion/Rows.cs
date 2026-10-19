namespace Meridian.Domain.Ingestion;

/// <summary>Column names of the Assembly export (Appendix A). Also used as field names in problems and normalizations.</summary>
public static class ExportColumns
{
    public const string AssemblyVoteId = "assembly_vote_id";
    public const string Session = "session";
    public const string VoteDate = "vote_date";
    public const string Title = "resolution_title";
    public const string CountryCode = "country_code";
    public const string CountryName = "country_name";
    public const string Ballot = "ballot";
    public const string PublishedAt = "published_at";

    public const int Count = 8;

    public static readonly IReadOnlyList<string> All =
        [AssemblyVoteId, Session, VoteDate, Title, CountryCode, CountryName, Ballot, PublishedAt];

    /// <summary>
    /// File-level rule (ING-07: only this fails a whole file): every Appendix A column must be present, and no unknown or
    /// repeated column is allowed. Columns are matched by exact name, in any order.
    /// </summary>
    public static HeaderCheck CheckHeader(IReadOnlyList<string> header) => new(
        All.Except(header).ToList(),
        header.Except(All).Distinct().ToList(),
        header.Where(All.Contains).GroupBy(h => h).Where(g => g.Count() > 1).Select(g => g.Key).ToList());
}

public sealed record HeaderCheck(IReadOnlyList<string> Missing, IReadOnlyList<string> Unknown, IReadOnlyList<string> Repeated)
{
    public bool IsValid => Missing.Count == 0 && Unknown.Count == 0 && Repeated.Count == 0;

    public string Message
    {
        get
        {
            var parts = new List<string>();
            if (Missing.Count > 0) parts.Add($"missing column(s): {string.Join(", ", Missing)}");
            if (Unknown.Count > 0) parts.Add($"unknown column(s): {string.Join(", ", Unknown)}");
            if (Repeated.Count > 0) parts.Add($"repeated column(s): {string.Join(", ", Repeated)}");
            return "The header is not the Assembly export format (" + string.Join("; ", parts) + "). No row was processed.";
        }
    }
}

public enum Ballot { Y, N, A, X }

/// <summary>
/// One data line exactly as split by the CSV reader: no trimming, no parsing. Fields are null when the line has fewer
/// columns. <see cref="FieldCount"/> is the number of columns actually found.
/// </summary>
public sealed record RawRow(
    int LineNumber,
    int FieldCount,
    string? AssemblyVoteId,
    string? Session,
    string? VoteDate,
    string? Title,
    string? CountryCode,
    string? CountryName,
    string? Ballot,
    string? PublishedAt);

/// <summary>A value that was changed by one of the allowed normalizations (ING-13). Always shown in the import report.</summary>
public sealed record Normalization(string Field, string Original, string Normalized);

public enum RowProblemCode
{
    WrongFieldCount,
    MissingField,
    InvalidSession,
    InvalidDate,
    UnknownCountry,
    InvalidBallot,
    NotAMember,
}

/// <summary>Why a row is Invalid, with the value as found in the file.</summary>
public sealed record RowProblem(RowProblemCode Code, string Field, string Message, string? OriginalValue);

/// <summary>The details every row of a vote repeats. They must agree within a file (ING-05).</summary>
public sealed record VoteDetails(int Session, DateOnly VoteDate, string Title);

/// <summary>Result of validating one row: either <see cref="ValidRow"/> or <see cref="InvalidRow"/>.</summary>
public abstract record ValidatedRow(
    int LineNumber,
    string? AssemblyVoteId,
    IReadOnlyList<Normalization> Normalizations,
    IReadOnlyList<string> EmptyOptionalFields);

public sealed record ValidRow(
    int LineNumber,
    string AssemblyVoteId,
    VoteDetails Details,
    string CountryCode,
    Ballot Ballot,
    string? CountryName,
    string? PublishedAt,
    IReadOnlyList<Normalization> Normalizations,
    IReadOnlyList<string> EmptyOptionalFields)
    : ValidatedRow(LineNumber, AssemblyVoteId, Normalizations, EmptyOptionalFields);

/// <param name="Details">
/// The row's vote details when they are valid on their own (vote id, session, date and title), even though the
/// country part of the row is not (unknown code, non-member, bad ballot). Such a row still counts for the vote's
/// details (DECISIONS §1). Null when the row's own details are invalid or the column count is wrong.
/// </param>
public sealed record InvalidRow(
    int LineNumber,
    string? AssemblyVoteId,
    string? CountryCode,
    IReadOnlyList<RowProblem> Problems,
    IReadOnlyList<Normalization> Normalizations,
    IReadOnlyList<string> EmptyOptionalFields,
    VoteDetails? Details = null)
    : ValidatedRow(LineNumber, AssemblyVoteId, Normalizations, EmptyOptionalFields)
{
    public string Reason => string.Join("; ", Problems.Select(p => p.Message));
}
