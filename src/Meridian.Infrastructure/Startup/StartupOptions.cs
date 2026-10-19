namespace Meridian.Infrastructure.Startup;

/// <summary>Section "Database".</summary>
public sealed class DatabaseOptions
{
    /// <summary>Apply pending EF migrations at startup. True in Development and docker compose.</summary>
    public bool MigrateOnStartup { get; set; }
}

/// <summary>Section "Seed".</summary>
public sealed class SeedOptions
{
    /// <summary>Path to member_states.csv, relative to the content root. Empty skips the load.</summary>
    public string? MemberStatesPath { get; set; }
}

/// <summary>Thrown when the database cannot be brought into a usable state. Stops the API from starting.</summary>
public sealed class DatabaseStartupException(string message, Exception? inner = null) : Exception(message, inner);
