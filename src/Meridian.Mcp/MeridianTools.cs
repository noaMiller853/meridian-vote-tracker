using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Meridian.Mcp;

/// <summary>
/// What Claude can do in Meridian. All tools act as the configured Analyst account. Admin actions (positions, publishing)
/// are not offered, and there is no file upload: the file would have to travel through the model.
/// </summary>
[McpServerToolType]
public sealed class MeridianTools(MeridianApi api)
{
    [McpServerTool(Name = "list_imports"), Description("Import history (newest first) with status and counts per class.")]
    public Task<string> ListImports(CancellationToken ct) => api.GetAsync("/api/imports?take=20", ct);

    [McpServerTool(Name = "get_import_report"), Description("The stored report of one import: counts per class, conflicts opened, normalizations, warnings.")]
    public Task<string> GetImportReport([Description("Import id")] long id, CancellationToken ct) => api.GetAsync($"/api/imports/{id}/report", ct);

    [McpServerTool(Name = "list_conflicts"), Description("Conflicts waiting for a human decision, optionally for one vote (Assembly id such as MA/2024/009).")]
    public Task<string> ListConflicts([Description("Assembly vote id, optional")] string? assemblyVoteId, CancellationToken ct) =>
        api.GetAsync("/api/conflicts" + (string.IsNullOrWhiteSpace(assemblyVoteId) ? "" : $"?assemblyVoteId={Uri.EscapeDataString(assemblyVoteId)}"), ct);

    [McpServerTool(Name = "get_conflict"), Description("One conflict: the approved value against the new one, with the source file, uploader and time of each.")]
    public Task<string> GetConflict([Description("Conflict id")] long id, CancellationToken ct) => api.GetAsync($"/api/conflicts/{id}", ct);

    [McpServerTool(Name = "decide_conflict"),
     Description("Approve (the new value becomes approved data) or reject (the approved value stays) a conflict. This changes approved data and is recorded under the service account: call it only when the user explicitly asked for this decision.")]
    public Task<string> DecideConflict([Description("Conflict id")] long id, [Description("Approve or Reject")] string decision,
        [Description("Optional note kept with the decision")] string? note, CancellationToken ct)
    {
        if (decision is not ("Approve" or "Reject")) return Task.FromResult("decision must be exactly Approve or Reject.");
        return api.PostAsync($"/api/conflicts/{id}/decision", new { decision, note }, ct);
    }

    [McpServerTool(Name = "list_votes"), Description("Votes, optionally filtered by title/Assembly id text and by whether they have a position.")]
    public Task<string> ListVotes([Description("Text in the title or the Assembly id, optional")] string? text,
        [Description("true: only votes with a position; false: only votes without one; omit for all")] bool? hasPosition, CancellationToken ct)
    {
        var query = new List<string> { "take=50" };
        if (!string.IsNullOrWhiteSpace(text)) query.Add($"text={Uri.EscapeDataString(text)}");
        if (hasPosition is not null) query.Add($"hasPosition={hasPosition.Value.ToString().ToLowerInvariant()}");
        return api.GetAsync("/api/votes?" + string.Join('&', query), ct);
    }

    [McpServerTool(Name = "get_vote_timeline"), Description("Everything that happened in one vote: imports, conflicts, decisions, value and position changes.")]
    public Task<string> GetVoteTimeline([Description("Vote id (the numeric id from list_votes)")] long voteId, CancellationToken ct) =>
        api.GetAsync($"/api/votes/{voteId}/timeline", ct);

    [McpServerTool(Name = "list_snapshots"), Description("Snapshots (frozen index calculations), newest first, with status, quarter and whether published.")]
    public Task<string> ListSnapshots(CancellationToken ct) => api.GetAsync("/api/snapshots", ct);

    [McpServerTool(Name = "get_snapshot"), Description("One snapshot: the rules used, excluded votes and the country ranking.")]
    public Task<string> GetSnapshot([Description("Snapshot id")] long id, CancellationToken ct) => api.GetAsync($"/api/snapshots/{id}", ct);
}
