using System.Text.Json;

namespace Meridian.Infrastructure.Votes;

public sealed record ImportRef(long ImportId, string FileName);

/// <param name="Stance">For or Against (POS-01).</param>
/// <param name="Weight">1 (marginal) to 5 (critical).</param>
/// <param name="Version">Send it back as <c>expectedVersion</c> when changing the position.</param>
public sealed record PositionDto(string Stance, int Weight, int Version, string SetBy, DateTimeOffset SetAt);

/// <param name="HasPosition">False: the vote has no position and is not counted in a snapshot silently (POS-04).</param>
/// <param name="Position">Null when the vote has no position.</param>
public sealed record VoteSummary(
    long Id,
    string AssemblyVoteId,
    int Session,
    DateOnly VoteDate,
    string Title,
    int CountryVotes,
    bool HasPosition,
    PositionDto? Position,
    int PendingConflicts);

/// <param name="Total">All votes that match the filter, not only this page.</param>
/// <param name="NextAfterId">Pass as <c>afterId</c> for the next page; null on the last page.</param>
public sealed record VotePage(IReadOnlyList<VoteSummary> Items, int Total, long? NextAfterId);

/// <summary>The import that set each detail of the vote (HIS-02).</summary>
public sealed record VoteDetailSources(ImportRef Session, ImportRef VoteDate, ImportRef Title);

/// <param name="SourceLine">Line in the file of the source row (the header is line 1).</param>
/// <param name="ApprovedBy"><c>system</c> for a ballot ingested as New; the analyst for one set by a conflict decision.</param>
/// <param name="ChangedByDecision">The ballot was changed by an approved conflict, not taken as New from a file.</param>
public sealed record CountryVoteDto(
    string CountryCode,
    string CountryName,
    string Ballot,
    ImportRef Source,
    int SourceLine,
    string ApprovedBy,
    DateTimeOffset ApprovedAt,
    bool ChangedByDecision);

public sealed record VoteView(
    long Id,
    string AssemblyVoteId,
    int Session,
    DateOnly VoteDate,
    string Title,
    ImportRef FirstImport,
    VoteDetailSources Sources,
    DateTimeOffset CreatedAt,
    bool HasPosition,
    PositionDto? Position,
    int PendingConflicts,
    IReadOnlyList<CountryVoteDto> CountryVotes);

/// <param name="Action">
/// Imported, VoteCreated, BallotsAdded, ConflictOpened, ConflictApproved, ConflictRejected, BallotChanged,
/// VoteFieldChanged, DecisionAttemptRejected, PositionSet or PositionChanged.
/// </param>
/// <param name="Actor">Who did it: the uploader for Imported, <c>system</c> for what an import did, otherwise the user.</param>
/// <param name="Import">The file the change came from, when there is one.</param>
/// <param name="Summary">One line for the timeline, for example "BRA ballot changed from Y to A".</param>
/// <param name="OldValue">The previous value as stored in the audit trail, when there is one.</param>
/// <param name="NewValue">The new value as stored in the audit trail.</param>
public sealed record TimelineEntry(
    DateTimeOffset At,
    string Action,
    string Actor,
    ImportRef? Import,
    long? ConflictId,
    string Summary,
    JsonElement? OldValue,
    JsonElement? NewValue);

public sealed record VoteTimeline(long VoteId, string AssemblyVoteId, IReadOnlyList<TimelineEntry> Entries);

/// <param name="Changed">False when the same stance and weight were sent again: nothing was written or audited.</param>
public sealed record PositionResult(bool Changed, PositionDto Position);
