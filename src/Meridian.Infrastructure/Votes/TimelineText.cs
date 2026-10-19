using System.Text.Json;

namespace Meridian.Infrastructure.Votes;

/// <summary>One readable line per timeline entry, from the values stored in the audit trail.</summary>
internal static class TimelineText
{
    public static string Imported(string file, string uploader, IReadOnlyDictionary<string, int> c)
    {
        var parts = new[] { "new", "unchanged", "duplicate", "invalid", "contradiction", "conflict" }
            .Where(k => c[k] > 0).Select(k => $"{c[k]} {k}");
        var conflicts = new[]
            {
                ("conflictsOpened", "opened"), ("conflictsAlreadyPending", "already pending"),
                ("conflictsAlreadyRejected", "already rejected"), ("conflictsAlreadySuperseded", "already superseded"),
            }
            .Where(k => c[k.Item1] > 0).Select(k => $"{c[k.Item1]} {k.Item2}").ToList();
        var rows = c["rows"] == 1 ? "1 row" : $"{c["rows"]} rows";
        return $"{file} uploaded by {uploader}: {rows} ({string.Join(", ", parts)})" +
               (conflicts.Count > 0 ? $"; conflicts {string.Join(", ", conflicts)}" : "");
    }

    public static string Describe(string action, string actor, string? file, long? conflictId, JsonElement? oldValue, JsonElement? newValue)
    {
        var from = file is null ? "" : $" from {file}";
        string N(string name) => Str(newValue, name);
        string O(string name) => Str(oldValue, name);

        return action switch
        {
            "VoteCreated" => $"Vote created{from}: session {N("session")}, {N("voteDate")}, '{N("title")}'",
            "BallotsAdded" => $"{N("count")} {(N("count") == "1" ? "country" : "countries")} added{from}",
            "ConflictOpened" => $"Conflict {conflictId} opened on {N("target")}: approved {N("approvedValue")}, {file ?? "the file"} says {N("proposedValue")}",
            "ConflictApproved" => $"Conflict {conflictId} on {N("target")} approved by {actor}: {N("approvedValue")} -> {N("proposedValue")}{Note(newValue)}",
            "ConflictRejected" => $"Conflict {conflictId} on {N("target")} rejected by {actor}: {N("approvedValue")} stays, {N("proposedValue")} not taken{Note(newValue)}",
            "BallotChanged" => $"{N("countryCode")} ballot changed from {O("ballot")} to {N("ballot")}{from} (decision on conflict {conflictId})",
            "VoteFieldChanged" => $"{N("field")} changed from '{O("value")}' to '{N("value")}'{from} (decision on conflict {conflictId})",
            "DecisionAttemptRejected" => $"{actor} tried to {N("attempted").ToLowerInvariant()} conflict {conflictId}; it was already {O("status").ToLowerInvariant()} by {O("decidedBy")}",
            "PositionSet" => $"Position set by {actor}: {N("stance")}, weight {N("weight")}",
            "PositionChanged" => $"Position changed by {actor}: {O("stance")}, weight {O("weight")} -> {N("stance")}, weight {N("weight")}",
            _ => action,
        };
    }

    private static string Note(JsonElement? value)
    {
        var note = Str(value, "note");
        return note.Length == 0 ? "" : $" (note: {note})";
    }

    private static string Str(JsonElement? value, string name) =>
        value is { ValueKind: JsonValueKind.Object } v && v.TryGetProperty(name, out var p)
            ? p.ValueKind switch
            {
                JsonValueKind.String => p.GetString()!,
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                _ => p.GetRawText(),
            }
            : "";
}
