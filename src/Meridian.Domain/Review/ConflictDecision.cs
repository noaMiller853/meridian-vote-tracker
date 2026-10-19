namespace Meridian.Domain.Review;

/// <summary>CON-04: the analyst approves the proposed value or rejects it.</summary>
public enum Decision { Approve, Reject }

public enum DecisionOutcome
{
    /// <summary>The decision is applied.</summary>
    Apply,

    /// <summary>Someone else decided first (CON-05); nothing changes.</summary>
    AlreadyDecided,

    /// <summary>
    /// Approve on a conflict whose approved value changed after it was opened, without confirming the current value;
    /// nothing changes (DECISIONS §5).
    /// </summary>
    Stale,
}

/// <summary>
/// Whether a decision on a conflict may be applied. Pure: the caller locks the conflict and the approved value first.
/// </summary>
public static class ConflictDecision
{
    public const string Pending = "Pending";

    /// <summary>The approved value is no longer the one the conflict was opened against.</summary>
    public static bool IsStale(string approvedValueAtOpen, string currentApprovedValue) =>
        approvedValueAtOpen != currentApprovedValue;

    /// <param name="status">Current status of the conflict.</param>
    /// <param name="decision">Approve or Reject.</param>
    /// <param name="approvedValueAtOpen">The approved value when the conflict was opened.</param>
    /// <param name="currentApprovedValue">The approved value now.</param>
    /// <param name="expectedApprovedValue">
    /// The approved value the analyst saw and confirms, when approving a stale conflict on purpose.
    /// </param>
    public static DecisionOutcome Evaluate(string status, Decision decision, string approvedValueAtOpen,
        string currentApprovedValue, string? expectedApprovedValue)
    {
        if (status != Pending)
            return DecisionOutcome.AlreadyDecided;

        // Rejecting never changes approved data, so a stale conflict can always be rejected.
        if (decision == Decision.Reject)
            return DecisionOutcome.Apply;

        // A confirmed value must be the current one, whether or not the conflict is stale: never replace a value the
        // analyst has not seen (CON-05).
        if (expectedApprovedValue is not null)
            return expectedApprovedValue == currentApprovedValue ? DecisionOutcome.Apply : DecisionOutcome.Stale;

        return IsStale(approvedValueAtOpen, currentApprovedValue) ? DecisionOutcome.Stale : DecisionOutcome.Apply;
    }
}
