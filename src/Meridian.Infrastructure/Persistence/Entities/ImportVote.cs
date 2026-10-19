namespace Meridian.Infrastructure.Persistence.Entities;

/// <summary>What happened to one vote in one import (ING-14: "what happened in each vote").</summary>
public class ImportVote
{
    public long ImportId { get; set; }
    public string AssemblyVoteId { get; set; } = null!;

    /// <summary>The approved vote, when it exists (created now or earlier).</summary>
    public long? VoteId { get; set; }

    /// <summary>New, Existing, Contradiction or NoValidDetails (Domain VoteStatus).</summary>
    public string Status { get; set; } = null!;

    public bool VoteCreated { get; set; }

    public int RowCount { get; set; }
    public int NewCount { get; set; }
    public int UnchangedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int InvalidCount { get; set; }
    public int ContradictionCount { get; set; }
    public int ConflictCount { get; set; }

    public int ConflictsOpened { get; set; }
    public int ConflictsAlreadyPending { get; set; }
    public int ConflictsAlreadyRejected { get; set; }
    public int ConflictsAlreadySuperseded { get; set; }

    /// <summary>Vote-field changes (jsonb array of {field, approvedValue, proposedValue, action}).</summary>
    public string? FieldConflicts { get; set; }

    public string? ContradictionReason { get; set; }

    public Import Import { get; set; } = null!;
    public Vote? Vote { get; set; }
}
