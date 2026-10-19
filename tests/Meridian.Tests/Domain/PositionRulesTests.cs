using Meridian.Domain.Positions;

namespace Meridian.Tests.Domain;

/// <summary>POS-01..POS-03: validation of a position and what a request does to the stored one.</summary>
public class PositionRulesTests
{
    private static readonly Position For4 = new(Stance.For, 4);

    [Theory]
    [InlineData("For", 1, Stance.For)]
    [InlineData("against", 5, Stance.Against)]
    [InlineData(" FOR ", 3, Stance.For)]
    public void Valid_request_gives_the_position(string stance, int weight, Stance expected)
    {
        var errors = PositionRules.Validate(stance, weight, 0, out var position);

        Assert.Empty(errors);
        Assert.Equal(new Position(expected, weight), position);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Weight_outside_1_to_5_is_an_error_under_weight(int weight)
    {
        var error = Assert.Single(PositionRules.Validate("For", weight, 0, out var position));

        Assert.Equal("weight", error.Field);
        Assert.Equal($"The weight must be from 1 to 5; got {weight}.", error.Message);
        Assert.Null(position);
    }

    [Theory]
    [InlineData("Maybe")]
    [InlineData("Y")]
    [InlineData("0")]
    public void Unknown_stance_is_an_error_under_stance(string stance)
    {
        var error = Assert.Single(PositionRules.Validate(stance, 3, 0, out _));

        Assert.Equal(("stance", $"Unknown stance '{stance}'. Use For or Against."), (error.Field, error.Message));
    }

    [Fact]
    public void Every_missing_field_is_reported_under_its_name()
    {
        var errors = PositionRules.Validate(null, null, null, out var position);

        Assert.Equal(["stance", "weight", "expectedVersion"], errors.Select(e => e.Field));
        Assert.Null(position);
    }

    [Fact]
    public void Negative_expected_version_is_an_error() =>
        Assert.Equal("expectedVersion", Assert.Single(PositionRules.Validate("For", 3, -1, out _)).Field);

    [Fact]
    public void First_position_with_version_0_is_created() =>
        Assert.Equal(PositionOutcome.Create, PositionRules.Decide(For4, 0, null, 0));

    [Fact]
    public void First_position_with_a_version_other_than_0_is_a_mismatch() =>
        Assert.Equal(PositionOutcome.VersionMismatch, PositionRules.Decide(For4, 3, null, 0));

    [Fact]
    public void Change_with_the_current_version_is_an_update()
    {
        Assert.Equal(PositionOutcome.Update, PositionRules.Decide(new Position(Stance.Against, 4), 2, For4, 2));
        Assert.Equal(PositionOutcome.Update, PositionRules.Decide(new Position(Stance.For, 2), 2, For4, 2)); // weight only
    }

    [Fact]
    public void Change_with_an_old_version_is_a_mismatch()
    {
        // Two admins read version 2; the first saved, so the second still sends 2 against version 3.
        Assert.Equal(PositionOutcome.VersionMismatch, PositionRules.Decide(new Position(Stance.Against, 4), 2, For4, 3));
        Assert.Equal(PositionOutcome.VersionMismatch, PositionRules.Decide(new Position(Stance.Against, 4), 0, For4, 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)] // an old version too: nothing would be overwritten
    public void Same_values_again_are_unchanged(int expectedVersion) =>
        Assert.Equal(PositionOutcome.Unchanged, PositionRules.Decide(new Position(Stance.For, 4), expectedVersion, For4, 1));

    [Fact]
    public void Stance_maps_to_desired_outcome_and_back()
    {
        Assert.Equal(("Y", "N"), (PositionRules.ToDesiredOutcome(Stance.For), PositionRules.ToDesiredOutcome(Stance.Against)));
        Assert.Equal((Stance.For, Stance.Against), (PositionRules.FromDesiredOutcome("Y"), PositionRules.FromDesiredOutcome("N")));
        Assert.Throws<ArgumentOutOfRangeException>(() => PositionRules.FromDesiredOutcome("A"));
    }
}
