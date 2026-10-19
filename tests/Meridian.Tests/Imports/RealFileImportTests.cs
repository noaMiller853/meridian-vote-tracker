using Meridian.Api.Controllers;
using Meridian.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Meridian.Tests.Imports;

/// <summary>
/// The real files in the recommended order (spec section 5). Skipped when the data files are not available
/// (MERIDIAN_DATA_DIR, data/, or the folder above the repository).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RealFileImportTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Q1_then_rev2_then_q1_again_then_rev2_again()
    {
        var q1Path = DataFiles.Find("assembly_export_2024_q1.csv");
        var rev2Path = DataFiles.Find("assembly_export_2024_q1_rev2.csv");
        Skip.If(q1Path is null || rev2Path is null, "q1 / rev2 files not found.");
        var q1Csv = await DataFiles.ReadAllTextAsync(q1Path!);
        var rev2Csv = await DataFiles.ReadAllTextAsync(rev2Path!);
        await using var api = await TestApi.StartAsync(fixture);

        // 1. q1: first import of the quarter.
        var q1 = await api.ImportAsync("assembly_export_2024_q1.csv", q1Csv);
        Assert.Equal(new ImportCounts(New: 5731, Unchanged: 0, Duplicate: 3, Invalid: 11, Contradiction: 151, Conflict: 0, Normalized: 2), q1.Import.Counts);
        var q1Votes = q1.Report!.Value.GetProperty("votes");
        Assert.Equal(39, q1Votes.GetProperty("created").GetInt32());
        Assert.Equal("MA/2024/025", q1Votes.GetProperty("contradiction").GetProperty("assemblyVoteIds")[0].GetString());
        var invalid = (await api.GetAsync<ImportRowPage>($"/api/imports/{q1.Import.Id}/rows?class=Invalid")).Items;
        Assert.Equal([2143, 2842, 2846, 3139, 4854], invalid.Where(r => !r.Reason!.StartsWith("vote_date")).Select(r => r.Line));
        Assert.Equal(6, invalid.Count(r => r.Reason == "vote_date '21/02/2024' is not a valid yyyy-mm-dd date"));
        Assert.Equal(DateTimeOffset.Parse("2024-04-02T09:00:00Z"), q1.Import.PublishedAt);

        // 2. rev2: corrections, completions and a new vote.
        var rev2 = await api.ImportAsync("assembly_export_2024_q1_rev2.csv", rev2Csv);
        Assert.Equal(new ImportCounts(New: 378, Unchanged: 5576, Duplicate: 0, Invalid: 0, Contradiction: 0, Conflict: 6, Normalized: 0), rev2.Import.Counts);
        Assert.Equal("new conflicts opened: 8 (0 already waiting for a decision, 0 already rejected, 0 already superseded)",
            rev2.Report!.Value.GetProperty("conflicts").GetProperty("summary").GetString());

        await using (var db = api.Db())
        {
            var conflicts = await db.Conflicts.Where(c => c.Status == "Pending")
                .Select(c => c.Vote.AssemblyVoteId + " " + (c.CountryCode ?? c.FieldName) + ": " + c.ApprovedValue + " -> " + c.ProposedValue)
                .ToListAsync();
            Assert.Equal(
                [
                    "MA/2024/003 BRA: Y -> A", "MA/2024/003 IND: Y -> N", "MA/2024/009 title: Biological Diversity in Coastal Zones -> Biological Diversity in Coastal Zones: Revised",
                    "MA/2024/010 JPN: Y -> X", "MA/2024/014 vote_date: 2024-02-05 -> 2024-02-06", "MA/2024/019 ZAF: Y -> N",
                    "MA/2024/028 CAN: Y -> N", "MA/2024/028 MEX: Y -> A",
                ],
                conflicts.Order(StringComparer.Ordinal));

            // New vote 041; completion of 031 (80 rows in q1); 025 now has one title; 040 is not in rev2 and is untouched.
            Assert.Equal(rev2.Import.Id, await db.Votes.Where(v => v.AssemblyVoteId == "MA/2024/041").Select(v => v.FirstImportId).SingleAsync());
            Assert.Equal((80, 69), (await Ballots(db, "MA/2024/031", q1.Import.Id), await Ballots(db, "MA/2024/031", rev2.Import.Id)));
            Assert.Equal(149, await Ballots(db, "MA/2024/025", rev2.Import.Id));
            Assert.Equal((149, 0), (await Ballots(db, "MA/2024/040", q1.Import.Id), await Ballots(db, "MA/2024/040", rev2.Import.Id)));
            // The approved values behind the 6 ballot conflicts are still the q1 values (ING-08).
            Assert.Equal("Y", await db.CountryVotes.Where(c => c.Vote.AssemblyVoteId == "MA/2024/003" && c.CountryCode == "BRA").Select(c => c.Ballot).SingleAsync());
        }

        // 3. q1 again: nothing new, no new conflict.
        var q1Again = await api.ImportAsync("assembly_export_2024_q1.csv", q1Csv);
        Assert.Equal((0, 5731), (q1Again.Import.Counts.New, q1Again.Import.Counts.Unchanged));
        Assert.Equal(0, q1Again.Report!.Value.GetProperty("conflicts").GetProperty("opened").GetInt32());

        // 4. rev2 again: the 8 conflicts are already waiting; none is opened twice (ING-12).
        var rev2Again = await api.ImportAsync("assembly_export_2024_q1_rev2.csv", rev2Csv);
        Assert.Equal((0, 6), (rev2Again.Import.Counts.New, rev2Again.Import.Counts.Conflict));
        Assert.Equal("new conflicts opened: 0 (8 already waiting for a decision, 0 already rejected, 0 already superseded)",
            rev2Again.Report!.Value.GetProperty("conflicts").GetProperty("summary").GetString());
        await using (var db = api.Db())
            Assert.Equal(8, await db.Conflicts.CountAsync());
    }

    [SkippableFact]
    public async Task Historic_file_fails_cleanly_when_the_command_timeout_is_too_short()
    {
        var path = DataFiles.Find("assembly_historic.csv");
        Skip.If(path is null, "assembly_historic.csv not found.");
        var logs = new CapturingLoggerProvider();
        await using var api = await TestApi.StartAsync(fixture,
            services: s => s.AddSingleton<ILoggerProvider>(logs),
            settings: new Dictionary<string, string> { ["Imports:CommandTimeoutSeconds"] = "1" });

        var created = await api.UploadPathAsync(path!);
        var done = await api.WaitUntilFinishedAsync(created.ImportId, TimeSpan.FromMinutes(3));

        // The configured timeout reaches the import's commands; the import is rolled back and nothing is stored.
        Assert.Equal("Failed", done.Import.Status);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Exception?.ToString().Contains("Timeout", StringComparison.OrdinalIgnoreCase) == true);
        await using var db = api.Db();
        Assert.False(await db.ImportRows.AnyAsync());
        Assert.False(await db.Votes.AnyAsync());
    }

    private static Task<int> Ballots(Meridian.Infrastructure.Persistence.MeridianDbContext db, string vote, long importId) =>
        db.CountryVotes.CountAsync(c => c.Vote.AssemblyVoteId == vote && c.SourceImportId == importId);
}
