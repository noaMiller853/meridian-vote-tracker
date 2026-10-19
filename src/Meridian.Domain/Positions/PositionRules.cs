namespace Meridian.Domain.Positions;

/// <summary>POS-01: what the institute wanted to happen in the vote. Stored as desired_outcome Y (For) or N (Against).</summary>
public enum Stance { For, Against }

public sealed record Position(Stance Stance, int Weight);

/// <summary>A validation error under the name of the request field (POS-02).</summary>
public sealed record FieldError(string Field, string Message);

public enum PositionOutcome
{
    /// <summary>The vote has no position yet and the client expected none (version 0).</summary>
    Create,

    /// <summary>The client saw the current version; the position changes.</summary>
    Update,

    /// <summary>Same stance and weight as now: nothing is written, audited or versioned.</summary>
    Unchanged,

    /// <summary>Someone changed the position since the client read it; nothing is overwritten.</summary>
    VersionMismatch,
}

/// <summary>Position and weight of a vote (POS-01..POS-03, DECISIONS §6 and §11). Pure.</summary>
public static class PositionRules
{
    public const int MinWeight = 1;
    public const int MaxWeight = 5;

    /// <summary>Version a client sends when the vote has no position yet.</summary>
    public const int NoPositionVersion = 0;

    public static class Fields
    {
        public const string Stance = "stance";
        public const string Weight = "weight";
        public const string ExpectedVersion = "expectedVersion";
    }

    /// <summary>Every problem of the request, each under its field; the position when there is none.</summary>
    public static IReadOnlyList<FieldError> Validate(string? stance, int? weight, int? expectedVersion, out Position? position)
    {
        var errors = new List<FieldError>();
        position = null;

        // By name only: Enum.TryParse would also accept numbers such as "0".
        var name = Enum.GetNames<Stance>().FirstOrDefault(n => n.Equals(stance?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(stance))
            errors.Add(new(Fields.Stance, "The stance is required: For or Against."));
        else if (name is null)
            errors.Add(new(Fields.Stance, $"Unknown stance '{stance}'. Use For or Against."));

        if (weight is null)
            errors.Add(new(Fields.Weight, $"The weight is required: a whole number from {MinWeight} to {MaxWeight}."));
        else if (weight is < MinWeight or > MaxWeight)
            errors.Add(new(Fields.Weight, $"The weight must be from {MinWeight} to {MaxWeight}; got {weight}."));

        if (expectedVersion is null)
            errors.Add(new(Fields.ExpectedVersion,
                $"The expected version is required: the version you saw, or {NoPositionVersion} when the vote has no position yet."));
        else if (expectedVersion < NoPositionVersion)
            errors.Add(new(Fields.ExpectedVersion, $"The expected version cannot be negative; got {expectedVersion}."));

        if (errors.Count == 0)
            position = new Position(Enum.Parse<Stance>(name!), weight!.Value);
        return errors;
    }

    /// <param name="requested">The validated request.</param>
    /// <param name="expectedVersion">The version the client saw; <see cref="NoPositionVersion"/> for none.</param>
    /// <param name="current">The position now, or null.</param>
    /// <param name="currentVersion">Its version (ignored when there is none).</param>
    public static PositionOutcome Decide(Position requested, int expectedVersion, Position? current, int currentVersion)
    {
        // Sending what is already stored overwrites nothing, so even an old version is not a conflict.
        if (current == requested)
            return PositionOutcome.Unchanged;

        if (current is null)
            return expectedVersion == NoPositionVersion ? PositionOutcome.Create : PositionOutcome.VersionMismatch;

        return expectedVersion == currentVersion ? PositionOutcome.Update : PositionOutcome.VersionMismatch;
    }

    public static string ToDesiredOutcome(Stance stance) => stance == Stance.For ? "Y" : "N";

    public static Stance FromDesiredOutcome(string desiredOutcome) => desiredOutcome switch
    {
        "Y" => Stance.For,
        "N" => Stance.Against,
        _ => throw new ArgumentOutOfRangeException(nameof(desiredOutcome), desiredOutcome, "desired_outcome is Y or N."),
    };
}
