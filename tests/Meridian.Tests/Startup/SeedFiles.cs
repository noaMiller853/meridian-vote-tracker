namespace Meridian.Tests.Startup;

internal static class SeedFiles
{
    /// <summary>The real seed/member_states.csv of the repository.</summary>
    public static string RealMemberStates { get; } = Path.Combine(RepositoryRoot(), "seed", "member_states.csv");

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Meridian.sln")))
                return dir.FullName;
        throw new InvalidOperationException("Repository root (Meridian.sln) not found above " + AppContext.BaseDirectory);
    }
}
