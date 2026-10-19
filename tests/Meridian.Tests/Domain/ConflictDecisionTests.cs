using Meridian.Domain.Review;

namespace Meridian.Tests.Domain;

/// <summary>CON-04, CON-05 and stale conflicts (DECISIONS §5). Example: FRA approved Y, conflicts Y->N and Y->A pending.</summary>
public class ConflictDecisionTests
{
    [Theory]
    [InlineData(Decision.Approve)]
    [InlineData(Decision.Reject)]
    public void Pending_conflict_on_an_unchanged_value_is_applied(Decision decision) =>
        Assert.Equal(DecisionOutcome.Apply, ConflictDecision.Evaluate("Pending", decision, "Y", "Y", null));

    [Theory]
    [InlineData("Approved", Decision.Approve)]
    [InlineData("Approved", Decision.Reject)]
    [InlineData("Rejected", Decision.Approve)]
    [InlineData("Rejected", Decision.Reject)]
    public void Decided_conflict_is_never_decided_again(string status, Decision decision) =>
        Assert.Equal(DecisionOutcome.AlreadyDecided, ConflictDecision.Evaluate(status, decision, "Y", "Y", null));

    [Fact]
    public void Decided_conflict_stays_decided_even_with_a_confirmed_value() =>
        Assert.Equal(DecisionOutcome.AlreadyDecided, ConflictDecision.Evaluate("Approved", Decision.Approve, "Y", "Y", "Y"));

    [Fact]
    public void Approve_after_another_approval_changed_the_value_is_stale()
    {
        // Y->N was approved, so Y->A was opened against a value that no longer exists.
        Assert.True(ConflictDecision.IsStale("Y", "N"));
        Assert.Equal(DecisionOutcome.Stale, ConflictDecision.Evaluate("Pending", Decision.Approve, "Y", "N", null));
    }

    [Fact]
    public void Stale_conflict_can_be_approved_when_the_current_value_is_confirmed() =>
        Assert.Equal(DecisionOutcome.Apply, ConflictDecision.Evaluate("Pending", Decision.Approve, "Y", "N", "N"));

    [Fact]
    public void Confirming_a_value_that_is_no_longer_current_is_stale()
    {
        // The analyst saw N, but someone changed it again before the request arrived.
        Assert.Equal(DecisionOutcome.Stale, ConflictDecision.Evaluate("Pending", Decision.Approve, "Y", "X", "N"));
        // The same holds when the conflict itself is not stale: never replace a value other than the one confirmed.
        Assert.Equal(DecisionOutcome.Stale, ConflictDecision.Evaluate("Pending", Decision.Approve, "Y", "Y", "N"));
    }

    [Fact]
    public void Stale_conflict_can_always_be_rejected() =>
        Assert.Equal(DecisionOutcome.Apply, ConflictDecision.Evaluate("Pending", Decision.Reject, "Y", "N", null));

    [Fact]
    public void Unchanged_value_is_not_stale() => Assert.False(ConflictDecision.IsStale("Y", "Y"));
}
