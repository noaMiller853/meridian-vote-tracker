using System.Globalization;
using System.Text.RegularExpressions;
using Meridian.Domain.Membership;

namespace Meridian.Domain.Ingestion;

/// <summary>
/// ING-04 checks for one row. Every problem is reported, not only the first.
/// The only normalizations (ING-13, DECISIONS §1): trim + upper-case country_code and ballot, and trim + collapse
/// internal whitespace in resolution_title. Each one is returned so the import report can show it.
/// </summary>
public static partial class RowValidator
{
    public static ValidatedRow Validate(RawRow raw, MembershipCalendar calendar)
    {
        // CSV format is a per-row check (ING-04): a line that could not be split is Invalid on its own (ING-07).
        if (raw.LineProblem is { } lineProblem)
            return new InvalidRow(raw.LineNumber, raw.AssemblyVoteId, null, [lineProblem], [], []);

        if (raw.FieldCount != ExportColumns.Count)
        {
            // The fields cannot be trusted when columns are missing or extra, so this is the only problem reported.
            var problem = new RowProblem(RowProblemCode.WrongFieldCount, "row",
                $"expected {ExportColumns.Count} columns, found {raw.FieldCount}", null);
            return new InvalidRow(raw.LineNumber, raw.AssemblyVoteId, null, [problem], [], []);
        }

        var problems = new List<RowProblem>();
        var normalizations = new List<Normalization>();

        var voteId = Required(raw.AssemblyVoteId, ExportColumns.AssemblyVoteId, problems);

        int? session = null;
        if (Required(raw.Session, ExportColumns.Session, problems) is { } sessionText)
        {
            if (int.TryParse(sessionText, NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s > 0)
                session = s;
            else
                problems.Add(new(RowProblemCode.InvalidSession, ExportColumns.Session,
                    $"session '{sessionText}' is not a positive whole number", sessionText));
        }

        DateOnly? voteDate = null;
        if (Required(raw.VoteDate, ExportColumns.VoteDate, problems) is { } dateText)
        {
            if (DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                voteDate = d;
            else
                problems.Add(new(RowProblemCode.InvalidDate, ExportColumns.VoteDate,
                    $"vote_date '{dateText}' is not a valid yyyy-mm-dd date", dateText));
        }

        // A normalization is recorded only when the normalized value is accepted: "Yes" -> "YES" is still invalid,
        // and reporting it as normalized would be noise.
        var title = Normalize(raw.Title, CollapseWhitespace);
        if (Required(title, ExportColumns.Title, problems, raw.Title) is { } acceptedTitle)
            Record(ExportColumns.Title, raw.Title, acceptedTitle, normalizations);

        // The vote part of the row is judged on its own: it is usable for the vote even if the country part fails.
        var details = voteId is not null && session is { } sv && voteDate is { } dv && !string.IsNullOrWhiteSpace(title)
            ? new VoteDetails(sv, dv, title)
            : null;

        var countryCode = Normalize(raw.CountryCode, Upper);
        var knownCountry = false;
        if (Required(countryCode, ExportColumns.CountryCode, problems, raw.CountryCode) is { } code)
        {
            knownCountry = calendar.IsKnown(code);
            if (knownCountry)
                Record(ExportColumns.CountryCode, raw.CountryCode, code, normalizations);
            else
                problems.Add(new(RowProblemCode.UnknownCountry, ExportColumns.CountryCode,
                    $"country_code '{raw.CountryCode}' is not a member state", raw.CountryCode));
        }

        Ballot? ballot = null;
        if (Required(Normalize(raw.Ballot, Upper), ExportColumns.Ballot, problems, raw.Ballot) is { } b)
        {
            // Exact letters only: Enum.TryParse would also accept numbers and "y,n".
            ballot = b switch { "Y" => Ballot.Y, "N" => Ballot.N, "A" => Ballot.A, "X" => Ballot.X, _ => null };
            if (ballot is null)
                problems.Add(new(RowProblemCode.InvalidBallot, ExportColumns.Ballot,
                    $"ballot '{raw.Ballot}' is not one of Y, N, A, X", raw.Ballot));
            else
                Record(ExportColumns.Ballot, raw.Ballot, b, normalizations);
        }

        // Only checked when code and date are usable, otherwise the reason would be misleading.
        if (knownCountry && voteDate is { } date && !calendar.IsMember(countryCode!, date))
            problems.Add(new(RowProblemCode.NotAMember, ExportColumns.CountryCode,
                $"{countryCode} is not a member on {date:yyyy-MM-dd}", raw.CountryCode));

        var emptyOptional = new List<string>();
        if (string.IsNullOrWhiteSpace(raw.CountryName)) emptyOptional.Add(ExportColumns.CountryName);
        if (string.IsNullOrWhiteSpace(raw.PublishedAt)) emptyOptional.Add(ExportColumns.PublishedAt);

        if (problems.Count > 0)
            return new InvalidRow(raw.LineNumber, raw.AssemblyVoteId, countryCode, problems, normalizations, emptyOptional, details);

        return new ValidRow(
            raw.LineNumber,
            voteId!,
            details!,
            countryCode!,
            ballot!.Value,
            raw.CountryName,
            raw.PublishedAt,
            normalizations,
            emptyOptional);
    }

    /// <summary>Adds a MissingField problem when the value is empty; returns the value otherwise.</summary>
    private static string? Required(string? value, string field, List<RowProblem> problems, string? original = null)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return value;
        problems.Add(new(RowProblemCode.MissingField, field, $"{field} is empty", original ?? value));
        return null;
    }

    /// <summary>Applies an allowed normalization. Empty values are left alone so they are reported as missing.</summary>
    private static string? Normalize(string? value, Func<string, string> normalize) =>
        string.IsNullOrWhiteSpace(value) ? value : normalize(value);

    private static void Record(string field, string? original, string normalized, List<Normalization> normalizations)
    {
        if (original is not null && original != normalized)
            normalizations.Add(new(field, original, normalized));
    }

    private static string Upper(string value) => value.Trim().ToUpperInvariant();

    private static string CollapseWhitespace(string value) => Whitespace().Replace(value.Trim(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
