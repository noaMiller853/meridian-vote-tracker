using System.Globalization;
using System.Text;
using Meridian.Domain.Ingestion;
using Meridian.Domain.Membership;
using Meridian.Tests.Startup;

namespace Meridian.Tests.Domain;

/// <summary>Test helpers: real CSV lines pasted verbatim, and the real membership calendar from seed/member_states.csv.</summary>
internal static class Ingest
{
    public static MembershipCalendar RealCalendar { get; } = new(
        File.ReadAllLines(SeedFiles.RealMemberStates).Skip(1).Select(line => line.Split(','))
            .Select(f => new MembershipPeriod(
                f[0],
                DateOnly.Parse(f[2], CultureInfo.InvariantCulture),
                f[3].Length == 0 ? null : DateOnly.Parse(f[3], CultureInfo.InvariantCulture))));

    /// <summary>Splits one CSV line (quotes and "" escapes) into a <see cref="RawRow"/>, like the real reader will.</summary>
    public static RawRow Raw(int lineNumber, string csvLine)
    {
        var f = Split(csvLine);
        string? At(int i) => i < f.Count ? f[i] : null;
        return new RawRow(lineNumber, f.Count, At(0), At(1), At(2), At(3), At(4), At(5), At(6), At(7));
    }

    public static ValidatedRow Validate(int lineNumber, string csvLine) =>
        RowValidator.Validate(Raw(lineNumber, csvLine), RealCalendar);

    /// <summary>Validates numbered lines and classifies them as one vote.</summary>
    public static VoteClassification Classify(string voteId, ExistingVote? existing, params (int Line, string Csv)[] lines) =>
        VoteClassifier.Classify(voteId, lines.Select(l => Validate(l.Line, l.Csv)).ToList(), existing);

    public static ExistingVote Existing(
        int session, string date, string title,
        IDictionary<string, Ballot> ballots,
        IEnumerable<ConflictKey>? pending = null,
        IEnumerable<ConflictKey>? rejected = null) =>
        new(new VoteDetails(session, DateOnly.Parse(date, CultureInfo.InvariantCulture), title),
            new Dictionary<string, Ballot>(ballots),
            (pending ?? []).ToHashSet(),
            (rejected ?? []).ToHashSet());

    private static List<string> Split(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else current.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields;
    }
}
