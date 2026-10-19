using System.Net;
using System.Net.Http.Json;
using Meridian.Api.Controllers;
using Meridian.Infrastructure.Imports;
using Meridian.Tests.Persistence;
using Meridian.Tests.Startup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Meridian.Tests.Imports;

/// <summary>The real 4b pipeline end to end through the API: spec 7.2, the report endpoints, the transaction boundary.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ImportPipelineTests(PostgresFixture fixture)
{
    private const string Header = "assembly_vote_id,session,vote_date,resolution_title,country_code,country_name,ballot,published_at";

    private static string Csv(params string[] rows) => string.Join("\n", [Header, .. rows]) + "\n";

    private static string Row(string vote, string title, string code, string name, string ballot) =>
        $"{vote},79,2024-01-11,{title},{code},{name},{ballot},2024-01-12T09:00:00Z";

    // Spec 7.2, first upload (rows 1 to 8 of the example are lines 2 to 9 of the file).
    private static readonly string Original = Csv(
        Row("101", "T101", "BRA", "Brazil", "Y"),
        Row("101", "T101", "IND", "India", "Y"),
        Row("102", "T102", "JPN", "Japan", "Y"),
        Row("102", "T102", "JPN", "Japan", "Y"),
        Row("103", "T103", "FRA", "France", "Y"),
        Row("103", "T103", "FRA", "France", "N"),
        Row("104", "T104", "FRN", "France", "X"),
        Row("104", "T104", "NZL", "New Zealand", "Y"));

    // Spec 7.2, second upload (the corrected file). The title of 101 changed; 102 is not in the file.
    private static readonly string Corrected = Csv(
        Row("101", "T101 corrected", "BRA", "Brazil", "A"),
        Row("101", "T101 corrected", "IND", "India", "Y"),
        Row("103", "T103", "FRA", "France", "N"),
        Row("104", "T104", "FRA", "France", "X"),
        Row("105", "T105", "BRA", "Brazil", "Y"));

    [Fact]
    public async Task Spec_7_2_original_then_corrected_then_corrected_again()
    {
        await using var api = await TestApi.StartAsync(fixture);

        // First upload.
        var first = await api.ImportAsync("original.csv", Original);
        var firstRows = await AllRows(api, first.Import.Id);
        Assert.Equal(
            [(2, "New"), (3, "New"), (4, "New"), (5, "Duplicate"), (6, "Contradiction"), (7, "Contradiction"), (8, "Invalid"), (9, "Invalid")],
            firstRows.Select(r => (r.Line, r.Classification)));
        Assert.Equal("identical to line 4", firstRows[3].Reason);
        Assert.Equal("NZL is not a member on 2024-01-11", firstRows[7].Reason);

        await using (var db = api.Db())
        {
            // Votes 103 and 104 exist although none of their ballots was ingested (DECISIONS §1).
            Assert.Equal(["101", "102", "103", "104"], await db.Votes.OrderBy(v => v.AssemblyVoteId).Select(v => v.AssemblyVoteId).ToListAsync());
            Assert.Equal(["101 BRA Y", "101 IND Y", "102 JPN Y"],
                await db.CountryVotes.OrderBy(c => c.Vote.AssemblyVoteId).ThenBy(c => c.CountryCode)
                    .Select(c => c.Vote.AssemblyVoteId + " " + c.CountryCode + " " + c.Ballot).ToListAsync());
            Assert.All(await db.CountryVotes.Select(c => c.ApprovedBy.Name).ToListAsync(), n => Assert.Equal("system", n));
        }

        // Second upload: the corrected file.
        var second = await api.ImportAsync("corrected.csv", Corrected);
        var secondRows = await AllRows(api, second.Import.Id);
        Assert.Equal([(2, "Conflict"), (3, "Unchanged"), (4, "New"), (5, "New"), (6, "New")], secondRows.Select(r => (r.Line, r.Classification)));
        Assert.Equal(("approved Y, file says A", "Open"), (secondRows[0].Reason, secondRows[0].ConflictAction));
        Assert.Equal("new conflicts opened: 2 (0 already waiting for a decision, 0 already rejected)",
            second.Report!.Value.GetProperty("conflicts").GetProperty("summary").GetString());

        await using (var db = api.Db())
        {
            // The approved value is not changed by the conflict (ING-08); the vote that is not in the file is untouched (ING-09).
            Assert.Equal("Y", await db.CountryVotes.Where(c => c.Vote.AssemblyVoteId == "101" && c.CountryCode == "BRA").Select(c => c.Ballot).SingleAsync());
            Assert.Equal(first.Import.Id, await db.CountryVotes.Where(c => c.Vote.AssemblyVoteId == "102").Select(c => c.SourceImportId).SingleAsync());
            Assert.False(await db.ImportVotes.AnyAsync(v => v.ImportId == second.Import.Id && v.AssemblyVoteId == "102"));
            var conflicts = await db.Conflicts.OrderBy(c => c.Kind)
                .Select(c => new { c.Kind, Target = c.CountryCode ?? c.FieldName, c.ApprovedValue, c.ProposedValue, c.Status, c.ApprovedSourceImportId, c.ProposedSourceImportId })
                .ToListAsync();
            Assert.Equal(
                [new { Kind = "CountryVote", Target = (string?)"BRA", ApprovedValue = "Y", ProposedValue = "A", Status = "Pending", ApprovedSourceImportId = first.Import.Id, ProposedSourceImportId = second.Import.Id },
                 new { Kind = "VoteField", Target = (string?)"title", ApprovedValue = "T101", ProposedValue = "T101 corrected", Status = "Pending", ApprovedSourceImportId = first.Import.Id, ProposedSourceImportId = second.Import.Id }],
                conflicts);
            // The completions of 103 and 104 and the new vote 105.
            Assert.Equal(["103 FRA N", "104 FRA X", "105 BRA Y"],
                await db.CountryVotes.Where(c => c.SourceImportId == second.Import.Id).OrderBy(c => c.Vote.AssemblyVoteId)
                    .Select(c => c.Vote.AssemblyVoteId + " " + c.CountryCode + " " + c.Ballot).ToListAsync());
        }

        // Dana uploads the corrected file again: 0 new data and 0 new conflicts.
        var again = await api.ImportAsync("corrected.csv", Corrected);
        Assert.Equal((0, 4, 1), (again.Import.Counts.New, again.Import.Counts.Unchanged, again.Import.Counts.Conflict));
        Assert.Equal("new conflicts opened: 0 (2 already waiting for a decision, 0 already rejected)",
            again.Report!.Value.GetProperty("conflicts").GetProperty("summary").GetString());
        await using (var db = api.Db())
            Assert.Equal(2, await db.Conflicts.CountAsync());
    }

    [Fact]
    public async Task Report_rows_and_votes_are_paged_and_filtered()
    {
        await using var api = await TestApi.StartAsync(fixture);
        var codes = File.ReadAllLines(SeedFiles.RealMemberStates).Skip(1).Select(l => l.Split(',')[0]).Distinct().ToList();
        var rows = codes.Select(c => Row("MA/2030/001", "Big vote", c, "Name", "Y")).ToList(); // lines 2..151, NZL is Invalid
        rows.Add(Row("MA/2030/001", "Big vote", "FRA", "France", "Y"));                         // line 152: Duplicate
        rows.Add("MA/2030/001,79,2024-01-11,\"Big vote,FRA,France,Y,");                          // line 153: MalformedLine
        rows.Add(Row("MA/2030/002", "Small vote", "DEU", "Germany", "N"));                       // line 154

        var import = await api.ImportAsync("big.csv", Csv([.. rows]));
        var id = import.Import.Id;
        Assert.Equal((150, 1, 2), (import.Import.Counts.New, import.Import.Counts.Duplicate, import.Import.Counts.Invalid));

        // Default: flagged rows only.
        var flagged = await api.GetAsync<ImportRowPage>($"/api/imports/{id}/rows");
        Assert.Equal(["Invalid", "Duplicate", "Invalid"], flagged.Items.Select(r => r.Classification));
        Assert.Equal("malformed CSV line: a quote opened at position 27 is never closed", flagged.Items[2].Reason);
        Assert.Null(flagged.NextAfter);

        // All rows, two pages by line.
        var page1 = await api.GetAsync<ImportRowPage>($"/api/imports/{id}/rows?class=All&take=100");
        var page2 = await api.GetAsync<ImportRowPage>($"/api/imports/{id}/rows?class=All&take=100&after={page1.NextAfter}");
        Assert.Equal((100, (int?)101), (page1.Items.Count, page1.NextAfter));
        Assert.Equal((53, (int?)null), (page2.Items.Count, page2.NextAfter));
        Assert.Equal(Enumerable.Range(2, 153), page1.Items.Concat(page2.Items).Select(r => r.Line));

        // Class and vote filters.
        Assert.Single((await api.GetAsync<ImportRowPage>($"/api/imports/{id}/rows?class=New&vote=MA/2030/002")).Items);
        Assert.Equal(2, (await api.GetAsync<ImportRowPage>($"/api/imports/{id}/rows?class=Invalid,Duplicate&take=2")).Items.Count);
        using (var client = api.Client())
        {
            var bad = await client.GetAsync($"/api/imports/{id}/rows?class=Bogus");
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Equal("Unknown class", (await bad.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/imports/999999/rows")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/imports/{id}/vote?assemblyVoteId=nope")).StatusCode);
        }

        // Votes, paged by vote id, and one vote with its rows.
        var votes1 = await api.GetAsync<ImportVotePage>($"/api/imports/{id}/votes?take=1");
        var votes2 = await api.GetAsync<ImportVotePage>($"/api/imports/{id}/votes?take=1&after={votes1.NextAfter}");
        Assert.Equal(["MA/2030/001", "MA/2030/002"], votes1.Items.Concat(votes2.Items).Select(v => v.AssemblyVoteId));
        var big = await api.GetAsync<ImportVoteDetails>($"/api/imports/{id}/vote?assemblyVoteId=MA/2030/001");
        Assert.Equal(("New", true, 151, 149, 1, 1), (big.Vote.Status, big.Vote.VoteCreated, big.Vote.Rows, big.Vote.Counts.New, big.Vote.Counts.Duplicate, big.Vote.Counts.Invalid));
        Assert.Equal(151, big.Rows.Count);

        // The stored summary.
        var report = await api.GetAsync<ImportReport>($"/api/imports/{id}/report");
        Assert.Equal("line in the file (the header is line 1)", report.Summary!.Value.GetProperty("lineNumbers").GetString());
        Assert.Equal(2, report.Summary.Value.GetProperty("votes").GetProperty("created").GetInt32());
        Assert.Equal(150, report.Counts.New);
    }

    [Fact]
    public async Task Mis_split_line_is_invalid_and_does_not_cost_its_vote()
    {
        // (a) The real historic case: 149 valid rows of MA/2017/216 (all members on 2017-05-01; TLS joined in 2023) and the
        // mis-split line 687843, whose unquoted comma in the title shifts every following field.
        await using var api = await TestApi.StartAsync(fixture);
        var codes = File.ReadAllLines(SeedFiles.RealMemberStates).Skip(1).Select(l => l.Split(',')[0]).Distinct().Where(c => c != "TLS").ToList();
        var rows = codes.Select(c => $"MA/2017/216,72,2017-05-01,\"Protection of Cultural Heritage, Including Capacity Building\",{c},Name,N,2017-05-03T09:00:00Z").ToList();
        rows.Insert(70, "MA/2017/216,72,2017-05-01,Protection of Cultural Heritage, Including Capacity Building,MEX,,N");

        var import = await api.ImportAsync("mis-split.csv", Csv([.. rows]));

        Assert.Equal((149, 1, 0), (import.Import.Counts.New, import.Import.Counts.Invalid, import.Import.Counts.Contradiction));
        var vote = await api.GetAsync<ImportVoteDetails>($"/api/imports/{import.Import.Id}/vote?assemblyVoteId=MA/2017/216");
        Assert.Equal(("New", true), (vote.Vote.Status, vote.Vote.VoteCreated));
        var invalid = Assert.Single(vote.Rows, r => r.Classification == "Invalid");
        Assert.Equal((72, "country_code ' Including Capacity Building' is not a member state; ballot is empty"), (invalid.Line, invalid.Reason));
        await using var db = api.Db();
        Assert.Equal("Protection of Cultural Heritage, Including Capacity Building", await db.Votes.Select(v => v.Title).SingleAsync());
        Assert.Equal(149, await db.CountryVotes.CountAsync());
    }

    [Fact]
    public async Task Two_valid_rows_with_truly_different_titles_make_the_vote_a_contradiction()
    {
        // (b) The contradiction rule (ING-06) still holds for real disagreements between valid rows.
        await using var api = await TestApi.StartAsync(fixture);

        var import = await api.ImportAsync("titles.csv", Csv(
            Row("MA/2024/090", "Ocean Plastic Pollution", "BRA", "Brazil", "Y"),
            Row("MA/2024/090", "Ocean Plastic Pollution in Coastal Zones", "IND", "India", "N")));

        Assert.Equal((0, 2), (import.Import.Counts.New, import.Import.Counts.Contradiction));
        var vote = await api.GetAsync<ImportVoteDetails>($"/api/imports/{import.Import.Id}/vote?assemblyVoteId=MA/2024/090");
        Assert.Equal(("Contradiction", false), (vote.Vote.Status, vote.Vote.VoteCreated));
        await using var db = api.Db();
        Assert.False(await db.Votes.AnyAsync());
    }

    [Fact]
    public async Task Page_size_is_capped_at_500_rows()
    {
        await using var api = await TestApi.StartAsync(fixture);
        var codes = File.ReadAllLines(SeedFiles.RealMemberStates).Skip(1).Select(l => l.Split(',')[0]).Distinct().ToList();
        var rows = Enumerable.Range(1, 4).SelectMany(v => codes.Select(c => Row($"MA/2031/00{v}", "T", c, "Name", "A"))).ToArray();

        var import = await api.ImportAsync("600.csv", Csv(rows));
        var page = await api.GetAsync<ImportRowPage>($"/api/imports/{import.Import.Id}/rows?class=All&take=100000");

        Assert.Equal(600, rows.Length);
        Assert.Equal((500, (int?)501), (page.Items.Count, page.NextAfter));
    }

    [Fact]
    public async Task Unclosed_quote_on_line_5_makes_only_line_5_invalid()
    {
        await using var api = await TestApi.StartAsync(fixture);
        var csv = Csv(
            Row("101", "T", "BRA", "Brazil", "Y"),
            Row("101", "T", "IND", "India", "Y"),
            Row("101", "T", "JPN", "Japan", "N"),
            "101,79,2024-01-11,\"T,DEU,Germany,Y,2024-01-12T09:00:00Z",  // line 5
            Row("101", "T", "ITA", "Italy", "A"),
            Row("101", "T", "ESP", "Spain", "X"));

        var import = await api.ImportAsync("quote.csv", csv);

        var all = await AllRows(api, import.Import.Id);
        Assert.Equal([(2, "New"), (3, "New"), (4, "New"), (5, "Invalid"), (6, "New"), (7, "New")], all.Select(r => (r.Line, r.Classification)));
        Assert.Equal("101,79,2024-01-11,\"T,DEU,Germany,Y,2024-01-12T09:00:00Z", all[3].RawLine);
    }

    [Fact]
    public async Task Job_row_is_not_locked_by_the_work_transaction()
    {
        var gate = new GateAfter();
        await using var api = await TestApi.StartAsync(fixture, services: s =>
        {
            s.AddScoped<VoteImportProcessor>();
            s.AddScoped<IImportProcessor>(sp => new GatedProcessor(sp.GetRequiredService<VoteImportProcessor>(), gate));
        });

        var created = await api.UploadAsync("original.csv", Original);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The real processor has written everything but the transaction is still open. The job row must not be locked,
        // otherwise progress writes from the other connection would wait for the whole import.
        await using (var connection = new NpgsqlConnection(api.ConnectionString))
        {
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await using var cmd = new NpgsqlCommand("SELECT id FROM background_jobs WHERE id = $1 FOR UPDATE NOWAIT", connection, tx);
            cmd.Parameters.Add(new() { Value = created.JobId });
            Assert.Equal(created.JobId, (long)(await cmd.ExecuteScalarAsync())!);
            await tx.RollbackAsync();
        }

        gate.Release.SetResult();
        Assert.Equal("Succeeded", (await api.WaitUntilFinishedAsync(created.ImportId)).Import.Status);
    }

    private static async Task<List<ImportRowDto>> AllRows(TestApi api, long importId) =>
        [.. (await api.GetAsync<ImportRowPage>($"/api/imports/{importId}/rows?class=All&take=500")).Items];

    private sealed class GateAfter
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Runs the real processor, then holds the work transaction open until the test releases it.</summary>
    private sealed class GatedProcessor(VoteImportProcessor inner, GateAfter gate) : IImportProcessor
    {
        public async Task<ImportProcessResult> ProcessAsync(ImportJob job, NpgsqlConnection connection, NpgsqlTransaction transaction,
            IJobProgress progress, CancellationToken ct)
        {
            var result = await inner.ProcessAsync(job, connection, transaction, progress, ct);
            gate.Reached.SetResult();
            await gate.Release.Task.WaitAsync(ct);
            return result;
        }
    }
}
