using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using Meridian.Domain.Membership;

namespace Meridian.Infrastructure.Startup;

public sealed record MemberStateRow(int Line, string CountryName, MembershipPeriod Period);

/// <summary>
/// Reads and validates seed/member_states.csv. The file is small reference data, so it is read fully.
/// Any problem fails the whole file: reference data that every import depends on must be correct.
/// </summary>
public sealed partial class MemberStatesFile
{
    private static readonly string[] RequiredColumns = ["country_code", "country_name", "member_from", "member_to"];

    public required string FileName { get; init; }
    public required string Sha256 { get; init; }
    public required IReadOnlyList<MemberStateRow> Rows { get; init; }

    public static MemberStatesFile Read(string path)
    {
        if (!File.Exists(path))
            throw new DatabaseStartupException($"Seed file not found: {path}. Check Seed:MemberStatesPath.");

        var bytes = File.ReadAllBytes(path);
        var errors = new List<string>();
        var rows = new List<MemberStateRow>();

        using (var reader = new StreamReader(new MemoryStream(bytes)))
        using (var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)))
        {
            if (!csv.Read() || !csv.ReadHeader())
                throw Invalid(path, ["the file is empty"]);

            var missing = RequiredColumns.Except(csv.HeaderRecord!).ToList();
            if (missing.Count > 0)
                throw Invalid(path, [$"missing column(s): {string.Join(", ", missing)}"]);

            while (csv.Read())
            {
                var line = csv.Parser.RawRow;
                var code = csv.GetField("country_code") ?? "";
                var name = csv.GetField("country_name") ?? "";
                var from = csv.GetField("member_from") ?? "";
                var to = csv.GetField("member_to") ?? "";

                var rowErrors = new List<string>();
                if (!CountryCode().IsMatch(code))
                    rowErrors.Add($"country_code '{code}' is not three upper-case letters");
                if (string.IsNullOrWhiteSpace(name))
                    rowErrors.Add("country_name is empty");
                if (!TryParseDate(from, out var fromDate))
                    rowErrors.Add($"member_from '{from}' is not a yyyy-mm-dd date");
                DateOnly? toDate = null;
                if (to.Length > 0)
                {
                    if (TryParseDate(to, out var parsed)) toDate = parsed;
                    else rowErrors.Add($"member_to '{to}' is not a yyyy-mm-dd date");
                }

                if (rowErrors.Count > 0)
                {
                    errors.AddRange(rowErrors.Select(e => $"line {line}: {e}"));
                    continue;
                }

                var period = new MembershipPeriod(code, fromDate, toDate);
                if (!period.IsValid)
                    errors.Add($"line {line}: {code} member_to {to} is before member_from {from}");
                else
                    rows.Add(new MemberStateRow(line, name, period));
            }
        }

        foreach (var (first, second) in MembershipRules.FindOverlaps(rows.Select(r => r.Period).ToList()))
            errors.Add($"line {rows[second].Line}: {rows[second].Period} overlaps line {rows[first].Line}: {rows[first].Period}");

        foreach (var group in rows.GroupBy(r => r.Period.CountryCode).Where(g => g.Select(r => r.CountryName).Distinct().Count() > 1))
            errors.Add($"{group.Key} has different names: {string.Join(", ", group.Select(r => $"'{r.CountryName}' (line {r.Line})"))}");

        if (errors.Count > 0)
            throw Invalid(path, errors);

        return new MemberStatesFile
        {
            FileName = Path.GetFileName(path),
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Rows = rows,
        };
    }

    private static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static DatabaseStartupException Invalid(string path, IEnumerable<string> errors) =>
        new($"Seed file {path} is invalid and was not loaded:{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", errors));

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CountryCode();
}
