using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meridian.Api.Controllers;
using Meridian.Infrastructure.Review;
using Meridian.Tests.Imports;
using Meridian.Tests.Persistence;
using Meridian.Tests.Startup;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Meridian.Tests.Review;

/// <summary>CON-01..CON-06, HIS-02, HIS-03 and ING-12 through the API, against a real PostgreSQL.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ConflictDecisionApiTests(PostgresFixture fixture)
{
    private const string Header = "assembly_vote_id,session,vote_date,resolution_title,country_code,country_name,ballot,published_at";

    private static string Csv(params string[] rows) => string.Join("\n", [Header, .. rows]) + "\n";

    private static string Row(string vote, string title, string code, string ballot) =>
        $"{vote},79,2024-01-11,{title},{code},Name,{ballot},2024-01-12T09:00:00Z";

    // Spec 7.2: the corrected file proposes BRA Y -> A and the title T101 -> "T101 corrected".
    private static readonly string Original = Csv(Row("101", "T101", "BRA", "Y"), Row("101", "T101", "IND", "Y"));
    private static readonly string Corrected = Csv(Row("101", "T101 corrected", "BRA", "A"), Row("101", "T101 corrected", "IND", "Y"));

    private sealed record Setup(TestApi Api, long OriginalImport, long CorrectedImport, long BallotConflict, long TitleConflict);

    private async Task<Setup> SpecExampleAsync()
    {
        var api = await TestApi.StartAsync(fixture);
        var original = await api.ImportAsync("original.csv", Original);
        var corrected = await api.ImportAsync("corrected.csv", Corrected);
        await using var db = api.Db();
        var ids = await db.Conflicts.ToDictionaryAsync(c => c.Kind, c => c.Id);
        return new Setup(api, original.Import.Id, corrected.Import.Id, ids["CountryVote"], ids["VoteField"]);
    }

    private static async Task<HttpResponseMessage> DecideAsync(TestApi api, long id, string decision, string user = "Dana",
        string? note = null, string? expected = null, string? correlationId = null)
    {
        using var client = api.Client(user, correlationId);
        return await client.PostAsJsonAsync($"/api/conflicts/{id}/decision", new { decision, note, expectedApprovedValue = expected });
    }

    private static async Task<ConflictDetails> OkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConflictDetails>())!;
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // ---- Approve and reject (CON-04, HIS-02, HIS-03) ----

    [Fact]
    public async Task Approve_makes_the_new_ballot_approved_and_keeps_the_previous_one()
    {
        var s = await SpecExampleAsync();
        await using var api = s.Api;

        var decided = await OkAsync(await DecideAsync(api, s.BallotConflict, "Approve", note: "Checked the voting sheet", correlationId: "corr-approve-1"));

        Assert.Equal(("Approved", "Dana", "Checked the voting sheet", "Y"),
            (decided.Status, decided.Decision!.DecidedBy, decided.Decision.Note, decided.Decision.ReplacedValue));
        Assert.Equal(s.OriginalImport, decided.Decision.ReplacedSource!.ImportId);
        Assert.Equal(("A", false), (decided.Current.Value, decided.Current.IsStale));

        await using var db = api.Db();
        var ballot = await db.CountryVotes.Where(c => c.Vote.AssemblyVoteId == "101" && c.CountryCode == "BRA")
            .Select(c => new { c.Ballot, c.SourceImportId, c.SourceRowNumber, Approver = c.ApprovedBy.Name }).SingleAsync();
        Assert.Equal(new { Ballot = "A", SourceImportId = s.CorrectedImport, SourceRowNumber = 2, Approver = "Dana" }, ballot);

        // HIS-02/HIS-03: the previous value and its source stay in the history, with who, when and the note.
        var events = await db.AuditEvents.Where(e => e.CorrelationId == "corr-approve-1").OrderBy(e => e.Id)
            .Select(e => new { e.Action, e.Entity, e.OldValue, e.NewValue, Actor = e.Actor.Name, e.ImportId }).ToListAsync();
        Assert.Equal(["ConflictApproved", "BallotChanged"], events.Select(e => e.Action));
        Assert.All(events, e => Assert.Equal(("Dana", s.CorrectedImport), (e.Actor, e.ImportId!.Value)));
        using (var old = JsonDocument.Parse(events[1].OldValue!))
            Assert.Equal(("Y", s.OriginalImport), (old.RootElement.GetProperty("ballot").GetString(), old.RootElement.GetProperty("sourceImportId").GetInt64()));
        using (var @new = JsonDocument.Parse(events[1].NewValue!))
            Assert.Equal("A", @new.RootElement.GetProperty("ballot").GetString());
        using (var decision = JsonDocument.Parse(events[0].NewValue!))
            Assert.Equal("Checked the voting sheet", decision.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Approve_of_a_title_moves_only_the_source_of_the_title()
    {
        var s = await SpecExampleAsync();
        await using var api = s.Api;

        var decided = await OkAsync(await DecideAsync(api, s.TitleConflict, "Approve", user: "Omar"));

        Assert.Equal(("T101 corrected", "T101"), (decided.Vote.Title, decided.Decision!.ReplacedValue));
        await using var db = api.Db();
        var vote = await db.Votes.SingleAsync(v => v.AssemblyVoteId == "101");
        Assert.Equal(("T101 corrected", s.CorrectedImport, s.OriginalImport, s.OriginalImport),
            (vote.Title, vote.TitleSourceImportId, vote.VoteDateSourceImportId, vote.SessionSourceImportId));
        Assert.Equal(["ConflictApproved", "VoteFieldChanged"],
            await db.AuditEvents.Where(e => e.Entity != "import" && e.Actor.Name == "Omar").OrderBy(e => e.Id).Select(e => e.Action).ToListAsync());
    }

    [Fact]
    public async Task Reject_keeps_the_approved_value_and_records_the_decision()
    {
        var s = await SpecExampleAsync();
        await using var api = s.Api;

        var decided = await OkAsync(await DecideAsync(api, s.BallotConflict, "reject", user: "Omar", note: "Typo in the export"));

        Assert.Equal(("Rejected", "Omar", "Typo in the export", (string?)null), (decided.Status, decided.Decision!.DecidedBy, decided.Decision.Note, decided.Decision.ReplacedValue));
        Assert.Equal("Y", decided.Current.Value);
        await using var db = api.Db();
        var ballot = await db.CountryVotes.Where(c => c.Vote.AssemblyVoteId == "101" && c.CountryCode == "BRA")
            .Select(c => new { c.Ballot, c.SourceImportId, Approver = c.ApprovedBy.Name }).SingleAsync();
        Assert.Equal(new { Ballot = "Y", SourceImportId = s.OriginalImport, Approver = "system" }, ballot);
        Assert.Equal(["ConflictRejected"],
            await db.AuditEvents.Where(e => e.Actor.Name == "Omar").Select(e => e.Action).ToListAsync());
    }

    // ---- Concurrency (CON-05) ----

    [Fact]
    public async Task Two_decisions_at_the_same_time_give_exactly_one_200_and_one_409()
    {
        // 20 ballot conflicts; for each one Dana approves and Omar rejects at the same moment.
        var codes = File.ReadAllLines(SeedFiles.RealMemberStates).Skip(1).Select(l => l.Split(','))
            .Where(f => f[3].Length == 0).Select(f => f[0]).Distinct().Take(20).ToList();
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("first.csv", Csv(codes.Select(c => Row("MA/2024/900", "Race", c, "Y")).ToArray()));
        await api.ImportAsync("second.csv", Csv(codes.Select(c => Row("MA/2024/900", "Race", c, "N")).ToArray()));
        List<long> ids;
        await using (var db = api.Db())
            ids = await db.Conflicts.OrderBy(c => c.Id).Select(c => c.Id).ToListAsync();
        Assert.Equal(20, ids.Count);

        using var dana = api.Client("Dana");
        using var omar = api.Client("Omar");
        foreach (var id in ids)
        {
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<HttpResponseMessage> Send(HttpClient client, string decision)
            {
                await go.Task;
                return await client.PostAsJsonAsync($"/api/conflicts/{id}/decision", new { decision });
            }
            var approve = Send(dana, "Approve");
            var reject = Send(omar, "Reject");
            go.SetResult();
            var responses = await Task.WhenAll(approve, reject);

            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
            var winner = responses.Single(r => r.StatusCode == HttpStatusCode.OK) == responses[0] ? "Dana" : "Omar";
            var problem = await ProblemAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict);
            Assert.Equal(ConflictsController.AlreadyDecidedType, problem.GetProperty("type").GetString());
            Assert.Equal(winner, problem.GetProperty("conflict").GetProperty("decision").GetProperty("decidedBy").GetString());
            Assert.Contains($"by {winner} at", problem.GetProperty("detail").GetString());
        }

        // Nothing was overwritten silently: each ballot matches the decision that won, and every loser is in the history.
        await using var check = api.Db();
        var outcome = await check.Conflicts.Select(c => new
        {
            c.Status,
            Ballot = check.CountryVotes.Where(v => v.VoteId == c.VoteId && v.CountryCode == c.CountryCode).Select(v => v.Ballot).Single(),
        }).ToListAsync();
        Assert.All(outcome, o => Assert.Equal(o.Status == "Approved" ? "N" : "Y", o.Ballot));
        Assert.Equal(20, await check.AuditEvents.CountAsync(e => e.Action == "DecisionAttemptRejected"));
        Assert.Equal(20, await check.AuditEvents.CountAsync(e => e.Action == "ConflictApproved" || e.Action == "ConflictRejected"));
    }

    [Fact]
    public async Task Decision_that_waits_for_another_one_on_the_same_conflict_gets_409()
    {
        var s = await SpecExampleAsync();
        await using var api = s.Api;

        // Omar's decision holds the conflict row in an open transaction while Dana's request arrives.
        await using var connection = new NpgsqlConnection(api.ConnectionString);
        await connection.OpenAsync();
        await using var omar = await connection.BeginTransactionAsync();
        await using (var lockRow = new NpgsqlCommand($"""
            UPDATE conflicts SET status = 'Rejected', decided_by_id = (SELECT id FROM users WHERE name = 'Omar'), decided_at = now(),
                                 decision_note = 'first'
            WHERE id = {s.BallotConflict} AND status = 'Pending'
            """, connection, omar))
            Assert.Equal(1, await lockRow.ExecuteNonQueryAsync());

        var dana = DecideAsync(api, s.BallotConflict, "Approve");
        await WaitUntilBlockedAsync(api.ConnectionString);
        Assert.False(dana.IsCompleted);

        await omar.CommitAsync();
        var problem = await ProblemAsync(await dana, HttpStatusCode.Conflict);

        Assert.Equal(ConflictsController.AlreadyDecidedType, problem.GetProperty("type").GetString());
        var decision = problem.GetProperty("conflict").GetProperty("decision");
        Assert.Equal(("Rejected", "Omar", "first"),
            (decision.GetProperty("decision").GetString(), decision.GetProperty("decidedBy").GetString(), decision.GetProperty("note").GetString()));
        await using var db = api.Db();
        Assert.Equal("Y", await db.CountryVotes.Where(c => c.CountryCode == "BRA").Select(c => c.Ballot).SingleAsync());
    }

    private static async Task WaitUntilBlockedAsync(string connectionString)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        for (var i = 0; i < 200; i++)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = $1 AND wait_event_type = 'Lock'", connection)
            { Parameters = { new() { Value = database } } };
            if ((long)(await cmd.ExecuteScalarAsync())! > 0)
                return;
            await Task.Delay(50);
        }
        throw new TimeoutException("The second decision never waited for the lock.");
    }

    [Fact]
    public async Task Deciding_a_decided_conflict_again_gives_409_with_the_decision_and_is_audited()
    {
        var s = await SpecExampleAsync();
        await using var api = s.Api;
        await OkAsync(await DecideAsync(api, s.BallotConflict, "Approve", note: "ok"));

        var problem = await ProblemAsync(await DecideAsync(api, s.BallotConflict, "Reject", user: "Omar"), HttpStatusCode.Conflict);

        Assert.Equal("Conflict already decided", problem.GetProperty("title").GetString());
        Assert.StartsWith($"Conflict {s.BallotConflict} was already approved by Dana at ", problem.GetProperty("detail").GetString());
        Assert.True(problem.TryGetProperty("correlationId", out _));
        await using var db = api.Db();
        var attempt = await db.AuditEvents.Where(e => e.Action == "DecisionAttemptRejected")
            .Select(e => new { Actor = e.Actor.Name, e.NewValue }).SingleAsync();
        Assert.Equal("Omar", attempt.Actor);
        using (var attempted = JsonDocument.Parse(attempt.NewValue!))
            Assert.Equal("Reject", attempted.RootElement.GetProperty("attempted").GetString());
        Assert.Equal("A", await db.CountryVotes.Where(c => c.CountryCode == "BRA").Select(c => c.Ballot).SingleAsync());
    }

    // ---- Stale conflicts (DECISIONS §5) ----

    [Fact]
    public async Task Approve_on_a_stale_conflict_needs_the_current_value_and_reject_is_always_allowed()
    {
        // FRA approved Y; file B proposes N, file C proposes A. N is approved first, so Y -> A is stale.
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("a.csv", Csv(Row("MA/2024/300", "T", "FRA", "Y")));
        var b = await api.ImportAsync("b.csv", Csv(Row("MA/2024/300", "T", "FRA", "N")));
        await api.ImportAsync("c.csv", Csv(Row("MA/2024/300", "T", "FRA", "A")));
        Dictionary<string, long> ids;
        await using (var db = api.Db())
            ids = await db.Conflicts.ToDictionaryAsync(c => c.ProposedValue, c => c.Id);
        await OkAsync(await DecideAsync(api, ids["N"], "Approve", user: "Omar"));

        var stale = await api.GetAsync<ConflictDetails>($"/api/conflicts/{ids["A"]}");
        Assert.Equal(("Y", "N", true), (stale.Approved.Value, stale.Current.Value, stale.Current.IsStale));
        Assert.Equal((ids["N"], "Omar"), (stale.Current.ChangedBy!.ConflictId, stale.Current.ChangedBy.DecidedBy));

        // Without the current value, and with a value that is not current: 409, nothing changes.
        var problem = await ProblemAsync(await DecideAsync(api, ids["A"], "Approve"), HttpStatusCode.Conflict);
        Assert.Equal(ConflictsController.StaleType, problem.GetProperty("type").GetString());
        Assert.Contains("The approved value is now 'N'; it was 'Y' when this conflict was opened", problem.GetProperty("detail").GetString());
        Assert.Contains("approved by Omar", problem.GetProperty("detail").GetString());
        Assert.Equal("Pending", problem.GetProperty("conflict").GetProperty("status").GetString());
        await ProblemAsync(await DecideAsync(api, ids["A"], "Approve", expected: "Y"), HttpStatusCode.Conflict);
        await using (var db = api.Db())
        {
            Assert.Equal("N", await db.CountryVotes.Select(c => c.Ballot).SingleAsync());
            Assert.Equal("Pending", await db.Conflicts.Where(c => c.Id == ids["A"]).Select(c => c.Status).SingleAsync());
        }

        // Confirming the current value approves it; the value replaced is N from file B, not Y.
        var decided = await OkAsync(await DecideAsync(api, ids["A"], "Approve", expected: "N"));
        Assert.Equal(("A", "N", b.Import.Id), (decided.Current.Value, decided.Decision!.ReplacedValue, decided.Decision.ReplacedSource!.ImportId));

        // Re-uploading B proposes N again: it was replaced by a decision, so nothing opens (AlreadySuperseded).
        var again = await api.ImportAsync("b.csv", Csv(Row("MA/2024/300", "T", "FRA", "N")));
        Assert.Contains("1 already superseded", again.Report!.Value.GetProperty("conflicts").GetProperty("summary").GetString());
        await using (var db = api.Db())
            Assert.Equal(2, await db.Conflicts.CountAsync());
    }

    [Fact]
    public async Task Stale_conflict_can_be_rejected_without_confirmation()
    {
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("a.csv", Csv(Row("MA/2024/301", "T", "FRA", "Y")));
        await api.ImportAsync("b.csv", Csv(Row("MA/2024/301", "T", "FRA", "N")));
        await api.ImportAsync("c.csv", Csv(Row("MA/2024/301", "T", "FRA", "A")));
        Dictionary<string, long> ids;
        await using (var db = api.Db())
            ids = await db.Conflicts.ToDictionaryAsync(c => c.ProposedValue, c => c.Id);
        await OkAsync(await DecideAsync(api, ids["N"], "Approve"));

        var decided = await OkAsync(await DecideAsync(api, ids["A"], "Reject"));

        Assert.Equal(("Rejected", "N"), (decided.Status, decided.Current.Value));
    }

    // ---- Reading (CON-01, CON-02, CON-06) ----

    [Fact]
    public async Task Conflict_shows_both_sides_with_their_source_and_stays_readable_after_the_decision()
    {
        var s = await SpecExampleAsync();
        await using var api = s.Api;

        var open = await api.GetAsync<ConflictDetails>($"/api/conflicts/{s.BallotConflict}");
        Assert.Equal(("Pending", "CountryVote", "BRA", "Brazil"), (open.Status, open.Kind, open.CountryCode, open.CountryName));
        Assert.Equal(("Y", "original.csv", "Dana", (int?)null), (open.Approved.Value, open.Approved.Source.FileName, open.Approved.Source.UploadedBy, open.Approved.Line));
        Assert.Equal(("A", "corrected.csv", 2), (open.Proposed.Value, open.Proposed.Source.FileName, open.Proposed.Line));
        Assert.Equal(DateTimeOffset.Parse("2024-01-12T09:00:00Z"), open.Proposed.Source.PublishedAt);
        Assert.Null(open.Decision);

        await OkAsync(await DecideAsync(api, s.BallotConflict, "Approve", user: "Lena", note: "Admin decided"));

        var decided = await api.GetAsync<ConflictDetails>($"/api/conflicts/{s.BallotConflict}");
        Assert.Equal(("Approved", "Lena", "Admin decided"), (decided.Status, decided.Decision!.DecidedBy, decided.Decision.Note));
        Assert.Equal(("Y", "A"), (decided.Approved.Value, decided.Proposed.Value)); // the conflict itself never changes

        var pending = await api.GetAsync<ConflictPage>("/api/conflicts");
        Assert.Equal([s.TitleConflict], pending.Items.Select(i => i.Id));
        var approved = await api.GetAsync<ConflictPage>("/api/conflicts?status=Approved");
        Assert.Equal((s.BallotConflict, "Lena"), (approved.Items.Single().Id, approved.Items.Single().DecidedBy));
        var all = await api.GetAsync<ConflictPage>("/api/conflicts?status=All&assemblyVoteId=101");
        Assert.Equal(2, all.Total);
        Assert.Empty((await api.GetAsync<ConflictPage>("/api/conflicts?status=All&assemblyVoteId=999")).Items);
        Assert.Equal([s.TitleConflict], (await api.GetAsync<ConflictPage>("/api/conflicts?kind=VoteField")).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Conflict_list_is_paged_by_id()
    {
        var s = await SpecExampleAsync();
        await using var api = s.Api;

        var first = await api.GetAsync<ConflictPage>("/api/conflicts?take=1");
        var second = await api.GetAsync<ConflictPage>($"/api/conflicts?take=1&afterId={first.NextAfterId}");

        Assert.Equal((2, 1), (first.Total, first.Items.Count));
        Assert.Equal(new[] { s.BallotConflict, s.TitleConflict }.Order(), first.Items.Concat(second.Items).Select(i => i.Id));
        Assert.Null(second.NextAfterId);
    }

    // ---- Roles and validation ----

    [Fact]
    public async Task Analysts_and_the_admin_decide_others_are_refused()
    {
        var s = await SpecExampleAsync();
        await using var api = s.Api;

        using (var anonymous = api.Client(user: null))
            await ProblemAsync(await anonymous.PostAsJsonAsync($"/api/conflicts/{s.BallotConflict}/decision", new { decision = "Approve" }),
                HttpStatusCode.Unauthorized);
        await ProblemAsync(await DecideAsync(api, s.BallotConflict, "Approve", user: "Mallory"), HttpStatusCode.Unauthorized);
        await ProblemAsync(await DecideAsync(api, s.BallotConflict, "Approve", user: "system"), HttpStatusCode.Forbidden);
        await using (var db = api.Db())
            Assert.Equal(2, await db.Conflicts.CountAsync(c => c.Status == "Pending"));

        await OkAsync(await DecideAsync(api, s.BallotConflict, "Approve", user: "Omar"));   // Analyst
        await OkAsync(await DecideAsync(api, s.TitleConflict, "Reject", user: "Lena"));     // Admin
    }

    [Theory]
    [InlineData(null, "decision")]
    [InlineData("Maybe", "decision")]
    [InlineData("0", "decision")]
    public async Task Bad_decision_points_to_the_field(string? decision, string field)
    {
        var s = await SpecExampleAsync();
        await using var api = s.Api;

        var problem = await ProblemAsync(await DecideAsync(api, s.BallotConflict, decision!), HttpStatusCode.BadRequest);

        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Note_too_long_unknown_conflict_and_unknown_status_are_reported()
    {
        var s = await SpecExampleAsync();
        await using var api = s.Api;

        var note = await ProblemAsync(await DecideAsync(api, s.BallotConflict, "Approve", note: new string('x', 1001)), HttpStatusCode.BadRequest);
        Assert.True(note.GetProperty("errors").TryGetProperty("note", out _));
        await ProblemAsync(await DecideAsync(api, 999_999, "Approve"), HttpStatusCode.NotFound);
        using var client = api.Client();
        await ProblemAsync(await client.GetAsync("/api/conflicts/999999"), HttpStatusCode.NotFound);
        var status = await ProblemAsync(await client.GetAsync("/api/conflicts?status=Open"), HttpStatusCode.BadRequest);
        Assert.True(status.GetProperty("errors").TryGetProperty("status", out _));
        await using var db = api.Db();
        Assert.Equal(2, await db.Conflicts.CountAsync(c => c.Status == "Pending"));
    }

    // ---- Re-upload after a decision (ING-12) ----

    [Fact]
    public async Task Re_upload_after_decisions_opens_nothing()
    {
        var s = await SpecExampleAsync();
        await using var api = s.Api;
        await OkAsync(await DecideAsync(api, s.BallotConflict, "Approve"));  // BRA Y -> A
        await OkAsync(await DecideAsync(api, s.TitleConflict, "Reject"));    // the title stays T101

        // The corrected file again: BRA A is now approved (Unchanged); the rejected title is AlreadyRejected.
        var corrected = await api.ImportAsync("corrected.csv", Corrected);
        Assert.Equal((0, 2, 0), (corrected.Import.Counts.New, corrected.Import.Counts.Unchanged, corrected.Import.Counts.Conflict));
        Assert.Equal("new conflicts opened: 0 (0 already waiting for a decision, 1 already rejected, 0 already superseded)",
            corrected.Report!.Value.GetProperty("conflicts").GetProperty("summary").GetString());

        // The original file again: BRA Y was replaced by the approval, so it stays a Conflict row and opens nothing.
        var original = await api.ImportAsync("original.csv", Original);
        var bra = (await api.GetAsync<ImportRowPage>($"/api/imports/{original.Import.Id}/rows")).Items.Single();
        Assert.Equal(("Conflict", "AlreadySuperseded", "BRA"), (bra.Classification, bra.ConflictAction, bra.CountryCode));
        Assert.Equal("new conflicts opened: 0 (0 already waiting for a decision, 0 already rejected, 1 already superseded)",
            original.Report!.Value.GetProperty("conflicts").GetProperty("summary").GetString());
        var vote = (await api.GetAsync<ImportVotePage>($"/api/imports/{original.Import.Id}/votes")).Items.Single();
        Assert.Equal(1, vote.ConflictsAlreadySuperseded);

        await using var db = api.Db();
        Assert.Equal(2, await db.Conflicts.CountAsync());
        Assert.Equal("A", await db.CountryVotes.Where(c => c.CountryCode == "BRA").Select(c => c.Ballot).SingleAsync());
    }
}
