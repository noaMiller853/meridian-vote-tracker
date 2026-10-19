namespace Meridian.Infrastructure.Review;

/// <summary>Where a value came from (CON-02): which file, who uploaded it and when.</summary>
public sealed record ValueSource(long ImportId, string FileName, string UploadedBy, DateTimeOffset UploadedAt, DateTimeOffset? PublishedAt);

/// <param name="Line">Line in the file of the proposing row (the header is line 1).</param>
public sealed record ConflictSide(string Value, ValueSource Source, int? Line);

/// <summary>The approval of another conflict on the same target that set the current approved value.</summary>
public sealed record ApprovedChange(long ConflictId, string Value, string DecidedBy, DateTimeOffset DecidedAt);

/// <param name="Value">The approved value now.</param>
/// <param name="IsStale">
/// Pending only: the approved value is no longer the one the conflict was opened against. Approving then needs
/// <c>expectedApprovedValue</c> (DECISIONS §5).
/// </param>
/// <param name="ChangedBy">
/// When the approved value differs from the one the conflict was opened against: the approval that set it.
/// </param>
public sealed record CurrentApprovedValue(string Value, bool IsStale, ApprovedChange? ChangedBy);

/// <param name="Decision">Approved or Rejected.</param>
/// <param name="ReplacedValue">Approved only: the value the approval replaced.</param>
public sealed record ConflictDecisionDto(string Decision, string DecidedBy, DateTimeOffset DecidedAt, string? Note,
    string? ReplacedValue, ValueSource? ReplacedSource);

public sealed record ConflictVote(long VoteId, string AssemblyVoteId, int Session, DateOnly VoteDate, string Title);

/// <summary>One conflict with both sides, the approved value now and the decision if there is one (CON-02, CON-06).</summary>
/// <param name="Kind">CountryVote (a ballot) or VoteField (session, vote_date or title).</param>
public sealed record ConflictDetails(
    long Id,
    string Status,
    string Kind,
    ConflictVote Vote,
    string? CountryCode,
    string? CountryName,
    string? FieldName,
    ConflictSide Approved,
    ConflictSide Proposed,
    CurrentApprovedValue Current,
    ConflictDecisionDto? Decision,
    DateTimeOffset CreatedAt);

public sealed record ConflictListItem(
    long Id,
    string Status,
    string Kind,
    long VoteId,
    string AssemblyVoteId,
    string? CountryCode,
    string? CountryName,
    string? FieldName,
    string ApprovedValue,
    string ProposedValue,
    string ProposedFileName,
    DateTimeOffset CreatedAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt);

/// <param name="Total">All conflicts that match the filter, not only this page.</param>
/// <param name="NextAfterId">Pass as <c>afterId</c> for the next page; null on the last page.</param>
public sealed record ConflictPage(IReadOnlyList<ConflictListItem> Items, int Total, long? NextAfterId);
