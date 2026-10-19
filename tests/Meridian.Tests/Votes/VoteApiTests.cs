using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Meridian.Api.Controllers;
using Meridian.Infrastructure.Votes;
using Meridian.Tests.Imports;
using Meridian.Tests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Tests.Votes;

/// <summary>Votes screens (section 6), positions and weights (POS-01..POS-04) and the vote timeline (HIS-02..HIS-04).</summary>
[Collection(PostgresCollection.Name)]
public sealed class VoteApiTests(PostgresFixture fixture)
{
    private const string Header = "assembly_vote_id,session,vote_date,resolution_title,country_code,country_name,ballot,published_at";

    private static string Csv(params string[] rows) => string.Join("\n", [Header, .. rows]) + "\n";

    private static string Row(string vote, int session, string date, string title, string code, string ballot) =>
        $"{vote},{session},{date},{title},{code},Name,{ballot},2024-01-12T09:00:00Z";

    private static string[] Vote(string vote, int session, string date, string title) =>
        [Row(vote, session, date, title, "FRA", "Y"), Row(vote, session, date, title, "BRA", "N")];

    // Five votes with two countries each.
    private static readonly string FiveVotes = Csv(
    [
        .. Vote("MA/2023/001", 78, "2023-05-10", "Ocean Protection"),
        .. Vote("MA/2024/001", 79, "2024-01-11", "Desertification"),
        .. Vote("MA/2024/002", 79, "2024-02-20", "Nuclear Non-Proliferation"),
        .. Vote("MA/2024/003", 79, "2024-03-12", "\"Maritime Safety, Navigation and Trade\""),
        .. Vote("MA/2025/001", 80, "2025-01-15", "Water: 50% access by 2030"),
    ]);

    private static async Task<Dictionary<string, long>> VoteIdsAsync(TestApi api)
    {
        await using var db = api.Db();
        return await db.Votes.ToDictionaryAsync(v => v.AssemblyVoteId, v => v.Id);
    }

    private static async Task<HttpResponseMessage> SetAsync(TestApi api, long voteId, string? stance, int? weight, int? expectedVersion,
        string user = "Lena")
    {
        using var client = api.Client(user);
        return await client.PutAsJsonAsync($"/api/votes/{voteId}/position", new { stance, weight, expectedVersion });
    }

    private static async Task<HttpResponseMessage> PutRawAsync(TestApi api, long voteId, string json)
    {
        using var client = api.Client("Lena");
        return await client.PutAsync($"/api/votes/{voteId}/position", new StringContent(json, Encoding.UTF8, "application/json"));
    }

    private static async Task<PositionResult> OkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PositionResult>())!;
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string[] ErrorFields(JsonElement problem) =>
        problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order().ToArray();

    // ---- List (section 6, POS-04) ----

    [Fact]
    public async Task Votes_are_paged_by_id_with_the_total()
    {
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("five.csv", FiveVotes);

        var pages = new List<VotePage>();
        long? after = 0;
        while (after is not null)
        {
            pages.Add(await api.GetAsync<VotePage>($"/api/votes?take=2&afterId={after}"));
            after = pages[^1].NextAfterId;
        }

        Assert.Equal([2, 2, 1], pages.Select(p => p.Items.Count));
        Assert.All(pages, p => Assert.Equal(5, p.Total));
        var all = pages.SelectMany(p => p.Items).ToList();
        Assert.Equal(all.Select(v => v.Id).Order(), all.Select(v => v.Id));
        Assert.All(all, v => Assert.Equal((2, 0), (v.CountryVotes, v.PendingConflicts)));
        Assert.Equal("Maritime Safety, Navigation and Trade", all.Single(v => v.AssemblyVoteId == "MA/2024/003").Title);
    }

    [Theory]
    [InlineData("session=79", new[] { "MA/2024/001", "MA/2024/002", "MA/2024/003" })]
    [InlineData("dateFrom=2024-02-01&dateTo=2024-12-31", new[] { "MA/2024/002", "MA/2024/003" })]
    [InlineData("dateFrom=2024-03-12", new[] { "MA/2024/003", "MA/2025/001" })]
    [InlineData("dateTo=2024-01-11", new[] { "MA/2023/001", "MA/2024/001" })]
    [InlineData("text=nuclear", new[] { "MA/2024/002" })]
    [InlineData("text=2023", new[] { "MA/2023/001" })]                // in the Assembly id
    [InlineData("text=%25", new[] { "MA/2025/001" })]                 // "%" is matched literally
    [InlineData("text=_", new string[0])]                             // so is "_": no title has one (unescaped it matches all)
    [InlineData("assemblyVoteId=MA/2024/001", new[] { "MA/2024/001" })]
    [InlineData("assemblyVoteId=MA/2024", new string[0])]             // exact, not a prefix
    [InlineData("session=79&text=safety", new[] { "MA/2024/003" })]
    public async Task Votes_are_filtered(string query, string[] expected)
    {
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("five.csv", FiveVotes);

        var page = await api.GetAsync<VotePage>($"/api/votes?{query}");

        Assert.Equal(expected, page.Items.Select(v => v.AssemblyVoteId));
        Assert.Equal(expected.Length, page.Total);
    }

    [Fact]
    public async Task Date_range_backwards_is_a_400_under_dateFrom()
    {
        await using var api = await TestApi.StartAsync(fixture);
        using var client = api.Client();

        var problem = await ProblemAsync(await client.GetAsync("/api/votes?dateFrom=2024-12-31&dateTo=2024-01-01"), HttpStatusCode.BadRequest);

        Assert.Equal(["dateFrom"], ErrorFields(problem));
    }

    [Fact]
    public async Task Vote_without_a_position_is_marked_and_hasPosition_filters()
    {
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("five.csv", FiveVotes);
        var ids = await VoteIdsAsync(api);
        await OkAsync(await SetAsync(api, ids["MA/2024/002"], "Against", 5, 0));

        var with = await api.GetAsync<VotePage>("/api/votes?hasPosition=true");
        var without = await api.GetAsync<VotePage>("/api/votes?hasPosition=false");
        var all = await api.GetAsync<VotePage>("/api/votes");

        Assert.Equal(["MA/2024/002"], with.Items.Select(v => v.AssemblyVoteId));
        Assert.Equal(4, without.Total);
        // POS-04: a vote without a position is clearly marked, in the list and in the details.
        Assert.All(without.Items, v => Assert.Equal((false, (PositionDto?)null), (v.HasPosition, v.Position)));
        var set = all.Items.Single(v => v.AssemblyVoteId == "MA/2024/002");
        Assert.Equal((true, "Against", 5, 1, "Lena"), (set.HasPosition, set.Position!.Stance, set.Position.Weight, set.Position.Version, set.Position.SetBy));
        var details = await api.GetAsync<VoteView>($"/api/votes/{ids["MA/2024/001"]}");
        Assert.Equal((false, (PositionDto?)null), (details.HasPosition, details.Position));
    }

    // ---- Setting a position (POS-01..POS-03) ----

    [Fact]
    public async Task Only_the_admin_sets_a_position()
    {
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("five.csv", FiveVotes);
        var id = (await VoteIdsAsync(api))["MA/2024/001"];

        var analyst = await ProblemAsync(await SetAsync(api, id, "For", 3, 0, user: "Omar"), HttpStatusCode.Forbidden);
        Assert.Equal("Omar (Analyst) cannot set positions. Only the admin can (POS-01).", analyst.GetProperty("detail").GetString());
        await ProblemAsync(await SetAsync(api, id, "For", 3, 0, user: "Dana"), HttpStatusCode.Forbidden);
        await ProblemAsync(await SetAsync(api, id, "For", 3, 0, user: "system"), HttpStatusCode.Forbidden);
        using (var anonymous = api.Client(user: null))
            await ProblemAsync(await anonymous.PutAsJsonAsync($"/api/votes/{id}/position", new { stance = "For", weight = 3, expectedVersion = 0 }),
                HttpStatusCode.Unauthorized);
        await using (var db = api.Db())
            Assert.False(await db.VotePositions.AnyAsync());

        var set = await OkAsync(await SetAsync(api, id, "For", 3, 0));
        Assert.Equal((true, "For", 3, 1, "Lena"), (set.Changed, set.Position.Stance, set.Position.Weight, set.Position.Version, set.Position.SetBy));
    }

    [Theory]
    [InlineData("""{"stance":"For","weight":7,"expectedVersion":0}""", "weight", "The weight must be from 1 to 5; got 7.")]
    [InlineData("""{"stance":"For","weight":0,"expectedVersion":0}""", "weight", "The weight must be from 1 to 5; got 0.")]
    [InlineData("""{"stance":"Maybe","weight":3,"expectedVersion":0}""", "stance", "Unknown stance 'Maybe'. Use For or Against.")]
    [InlineData("""{"weight":3,"expectedVersion":0}""", "stance", "The stance is required: For or Against.")]
    [InlineData("""{"stance":"For","expectedVersion":0}""", "weight", "The weight is required: a whole number from 1 to 5.")]
    [InlineData("""{"stance":"For","weight":3}""", "expectedVersion", "The expected version is required: the version you saw, or 0 when the vote has no position yet.")]
    // JSON type errors, for the whole API: under the plain field name, not "$.weight" (POS-02).
    [InlineData("""{"stance":"For","weight":"abc","expectedVersion":0}""", "weight", "The value of weight has the wrong type or format.")]
    [InlineData("""{"stance":"For","weight":1.5,"expectedVersion":0}""", "weight", "The value of weight has the wrong type or format.")]
    [InlineData("""{"stance":"For","weight":3,"expectedVersion":"x"}""", "expectedVersion", "The value of expectedVersion has the wrong type or format.")]
    public async Task Validation_errors_are_under_the_field(string json, string field, string message)
    {
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("five.csv", FiveVotes);
        var id = (await VoteIdsAsync(api))["MA/2024/001"];

        var problem = await ProblemAsync(await PutRawAsync(api, id, json), HttpStatusCode.BadRequest);

        Assert.Equal([field], ErrorFields(problem));
        Assert.Equal([message], problem.GetProperty("errors").GetProperty(field).EnumerateArray().Select(e => e.GetString()));
        Assert.True(problem.TryGetProperty("correlationId", out _));
        await using var db = api.Db();
        Assert.False(await db.VotePositions.AnyAsync());
    }

    [Fact]
    public async Task Weight_7_and_a_bad_stance_are_reported_together()
    {
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("five.csv", FiveVotes);
        var id = (await VoteIdsAsync(api))["MA/2024/001"];

        var problem = await ProblemAsync(await SetAsync(api, id, "Yes", 7, 0), HttpStatusCode.BadRequest);

        Assert.Equal(["stance", "weight"], ErrorFields(problem));
    }

    [Fact]
    public async Task Body_that_is_not_json_is_a_400()
    {
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("five.csv", FiveVotes);
        var id = (await VoteIdsAsync(api))["MA/2024/001"];

        var problem = await ProblemAsync(await PutRawAsync(api, id, "{not json"), HttpStatusCode.BadRequest);

        Assert.Equal("The request body is not valid JSON.", problem.GetProperty("errors").GetProperty("").EnumerateArray().Single().GetString());
    }

    [Fact]
    public async Task Every_change_is_audited_with_previous_and_new_value_and_the_same_values_record_nothing()
    {
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("five.csv", FiveVotes);
        var id = (await VoteIdsAsync(api))["MA/2024/001"];

        await OkAsync(await SetAsync(api, id, "For", 4, 0));
        var changed = await OkAsync(await SetAsync(api, id, "Against", 4, 1));     // stance only
        var weight = await OkAsync(await SetAsync(api, id, "Against", 2, 2));      // weight only
        var again = await OkAsync(await SetAsync(api, id, "Against", 2, 3));       // same values
        var oldVersion = await OkAsync(await SetAsync(api, id, "against", 2, 1));  // same values, old version

        Assert.Equal((true, 2), (changed.Changed, changed.Position.Version));
        Assert.Equal((true, 3), (weight.Changed, weight.Position.Version));
        Assert.Equal((false, 3), (again.Changed, again.Position.Version));
        Assert.Equal((false, 3, weight.Position.SetAt), (oldVersion.Changed, oldVersion.Position.Version, oldVersion.Position.SetAt));

        await using var db = api.Db();
        var events = await db.AuditEvents.Where(e => e.Entity == "vote_position").OrderBy(e => e.Id)
            .Select(e => new { e.Action, e.EntityId, e.VoteId, Actor = e.Actor.Name, e.OldValue, e.NewValue }).ToListAsync();
        Assert.Equal(["PositionSet", "PositionChanged", "PositionChanged"], events.Select(e => e.Action));
        Assert.All(events, e => Assert.Equal((id.ToString(), id, "Lena"), (e.EntityId, e.VoteId!.Value, e.Actor)));
        Assert.Null(events[0].OldValue);
        Assert.Equal(("For", 4, 1), Values(events[0].NewValue!));
        Assert.Equal((("For", 4, 1), ("Against", 4, 2)), (Values(events[1].OldValue!), Values(events[1].NewValue!)));
        Assert.Equal((("Against", 4, 2), ("Against", 2, 3)), (Values(events[2].OldValue!), Values(events[2].NewValue!)));

        static (string, int, int) Values(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return (r.GetProperty("stance").GetString()!, r.GetProperty("weight").GetInt32(), r.GetProperty("version").GetInt32());
        }
    }

    [Fact]
    public async Task Change_based_on_an_old_version_is_a_409_with_the_current_position()
    {
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("five.csv", FiveVotes);
        var id = (await VoteIdsAsync(api))["MA/2024/001"];

        // A first position must be sent with version 0.
        var none = await ProblemAsync(await SetAsync(api, id, "For", 4, 3), HttpStatusCode.Conflict);
        Assert.Equal($"Vote {id} has no position, so expectedVersion must be 0; you sent 3. Nothing was changed.", none.GetProperty("detail").GetString());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("position").ValueKind);

        await OkAsync(await SetAsync(api, id, "For", 4, 0));
        var problem = await ProblemAsync(await SetAsync(api, id, "Against", 1, 0), HttpStatusCode.Conflict);

        Assert.Equal(VotesController.PositionChangedType, problem.GetProperty("type").GetString());
        Assert.StartsWith($"The position of vote {id} was set by Lena at ", problem.GetProperty("detail").GetString());
        Assert.EndsWith("to For, weight 4 (version 1); you sent expectedVersion 0. Nothing was changed. Check the current position, then resend with expectedVersion 1.",
            problem.GetProperty("detail").GetString());
        var position = problem.GetProperty("position");
        Assert.Equal(("For", 4, 1), (position.GetProperty("stance").GetString(), position.GetProperty("weight").GetInt32(), position.GetProperty("version").GetInt32()));
        await using var db = api.Db();
        Assert.Equal(new { DesiredOutcome = "Y", Weight = (short)4, Version = 1 },
            await db.VotePositions.Select(p => new { p.DesiredOutcome, p.Weight, p.Version }).SingleAsync());
        Assert.Equal(1, await db.AuditEvents.CountAsync(e => e.Entity == "vote_position"));
    }

    [Fact]
    public async Task Two_admins_changing_the_same_position_at_once_give_one_200_and_one_409()
    {
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("five.csv", FiveVotes);
        var id = (await VoteIdsAsync(api))["MA/2024/001"];
        using var first = api.Client("Lena");
        using var second = api.Client("Lena");
        var current = (await OkAsync(await SetAsync(api, id, "For", 3, 0))).Position;

        for (var round = 0; round < 20; round++)
        {
            // Both read the same version and send different values, neither equal to the current one.
            var opposite = current.Stance == "For" ? "Against" : "For";
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<HttpResponseMessage> Send(HttpClient client, string stance, int weight)
            {
                await go.Task;
                return await client.PutAsJsonAsync($"/api/votes/{id}/position", new { stance, weight, expectedVersion = current.Version });
            }
            var a = Send(first, opposite, current.Weight);
            var b = Send(second, current.Stance, current.Weight % 5 + 1);
            go.SetResult();
            var responses = await Task.WhenAll(a, b);

            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
            var winner = (await responses.Single(r => r.StatusCode == HttpStatusCode.OK).Content.ReadFromJsonAsync<PositionResult>())!;
            var problem = await ProblemAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict);
            Assert.Equal(winner.Position.Version, problem.GetProperty("position").GetProperty("version").GetInt32());
            Assert.Equal(current.Version + 1, winner.Position.Version);
            current = winner.Position;
        }

        await using var db = api.Db();
        Assert.Equal(21, await db.VotePositions.Select(p => p.Version).SingleAsync());
        Assert.Equal(21, await db.AuditEvents.CountAsync(e => e.Entity == "vote_position"));
    }

    [Fact]
    public async Task Two_first_positions_at_once_give_one_200_and_one_409()
    {
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("five.csv", FiveVotes);
        var ids = await VoteIdsAsync(api);
        using var first = api.Client("Lena");
        using var second = api.Client("Lena");

        foreach (var id in ids.Values)
        {
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<HttpResponseMessage> Send(HttpClient client, string stance)
            {
                await go.Task;
                return await client.PutAsJsonAsync($"/api/votes/{id}/position", new { stance, weight = 3, expectedVersion = 0 });
            }
            var a = Send(first, "For");
            var b = Send(second, "Against");
            go.SetResult();

            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], (await Task.WhenAll(a, b)).Select(r => r.StatusCode).Order());
        }

        await using var db = api.Db();
        Assert.Equal(5, await db.AuditEvents.CountAsync(e => e.Action == "PositionSet"));
    }

    [Fact]
    public async Task Unknown_vote_is_a_404()
    {
        await using var api = await TestApi.StartAsync(fixture);
        using var client = api.Client();

        await ProblemAsync(await client.GetAsync("/api/votes/999999"), HttpStatusCode.NotFound);
        await ProblemAsync(await client.GetAsync("/api/votes/999999/timeline"), HttpStatusCode.NotFound);
        await ProblemAsync(await SetAsync(api, 999_999, "For", 3, 0), HttpStatusCode.NotFound);
    }

    // ---- One vote and its timeline (HIS-02..HIS-04), on the spec 7.2 example ----

    private const string D = "2024-01-11";
    private static readonly string Original = Csv(Row("101", 79, D, "T101", "BRA", "Y"), Row("101", 79, D, "T101", "IND", "Y"));
    private static readonly string Corrected = Csv(Row("101", 79, D, "T101 corrected", "BRA", "A"), Row("101", 79, D, "T101 corrected", "IND", "Y"));

    private static async Task DecideAsync(TestApi api, string kind, string decision, string user, string? note = null)
    {
        long id;
        await using (var db = api.Db())
            id = await db.Conflicts.Where(c => c.Kind == kind).Select(c => c.Id).SingleAsync();
        using var client = api.Client(user);
        var response = await client.PostAsJsonAsync($"/api/conflicts/{id}/decision", new { decision, note });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Vote_shows_its_country_votes_with_their_source_and_the_decisions()
    {
        await using var api = await TestApi.StartAsync(fixture);
        var original = await api.ImportAsync("original.csv", Original);
        var corrected = await api.ImportAsync("corrected.csv", Corrected);
        await DecideAsync(api, "CountryVote", "Approve", "Dana", "Checked the sheet");
        var id = (await VoteIdsAsync(api))["101"];

        var vote = await api.GetAsync<VoteView>($"/api/votes/{id}");

        Assert.Equal(("101", 79, "T101", 1), (vote.AssemblyVoteId, vote.Session, vote.Title, vote.PendingConflicts)); // the title conflict waits
        Assert.Equal((original.Import.Id, "original.csv"), (vote.FirstImport.ImportId, vote.Sources.Title.FileName));
        var bra = vote.CountryVotes.Single(c => c.CountryCode == "BRA");
        Assert.Equal(("Brazil", "A", corrected.Import.Id, "corrected.csv", 2, "Dana", true),
            (bra.CountryName, bra.Ballot, bra.Source.ImportId, bra.Source.FileName, bra.SourceLine, bra.ApprovedBy, bra.ChangedByDecision));
        var ind = vote.CountryVotes.Single(c => c.CountryCode == "IND");
        Assert.Equal(("Y", "original.csv", 3, "system", false), (ind.Ballot, ind.Source.FileName, ind.SourceLine, ind.ApprovedBy, ind.ChangedByDecision));
        Assert.Equal(1, (await api.GetAsync<VotePage>("/api/votes")).Items.Single().PendingConflicts);
    }

    [Fact]
    public async Task Timeline_shows_everything_that_happened_in_the_vote_in_order()
    {
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("original.csv", Original);
        await api.ImportAsync("corrected.csv", Corrected);
        await DecideAsync(api, "CountryVote", "Approve", "Dana", "Checked the sheet");
        await DecideAsync(api, "VoteField", "Reject", "Omar");
        var id = (await VoteIdsAsync(api))["101"];
        await OkAsync(await SetAsync(api, id, "For", 4, 0));
        await OkAsync(await SetAsync(api, id, "For", 2, 1));
        await OkAsync(await SetAsync(api, id, "For", 2, 2));               // same values: not in the timeline
        await api.ImportAsync("corrected.csv", Corrected);                  // changes nothing: still shown

        var timeline = await api.GetAsync<VoteTimeline>($"/api/votes/{id}/timeline");

        Assert.Equal(
            ["Imported", "VoteCreated", "BallotsAdded",
             "Imported", "ConflictOpened", "ConflictOpened",
             "ConflictApproved", "BallotChanged", "ConflictRejected",
             "PositionSet", "PositionChanged",
             "Imported"],
            timeline.Entries.Select(e => e.Action));
        Assert.Equal(timeline.Entries.Select(e => e.At).Order(), timeline.Entries.Select(e => e.At));
        var conflicts = timeline.Entries.Where(e => e.Action == "ConflictOpened").Select(e => e.ConflictId!.Value).ToList();

        Assert.Equal(
            [
                "original.csv uploaded by Dana: 2 rows (2 new)",
                "Vote created from original.csv: session 79, 2024-01-11, 'T101'",
                "2 countries added from original.csv",
                "corrected.csv uploaded by Dana: 2 rows (1 unchanged, 1 conflict); conflicts 2 opened",
                $"Conflict {conflicts[0]} opened on BRA: approved Y, corrected.csv says A",
                $"Conflict {conflicts[1]} opened on title: approved T101, corrected.csv says T101 corrected",
                $"Conflict {conflicts[0]} on BRA approved by Dana: Y -> A (note: Checked the sheet)",
                $"BRA ballot changed from Y to A from corrected.csv (decision on conflict {conflicts[0]})",
                $"Conflict {conflicts[1]} on title rejected by Omar: T101 stays, T101 corrected not taken",
                "Position set by Lena: For, weight 4",
                "Position changed by Lena: For, weight 4 -> For, weight 2",
                "corrected.csv uploaded by Dana: 2 rows (2 unchanged); conflicts 1 already rejected",
            ],
            timeline.Entries.Select(e => e.Summary));

        // Who did it and where it came from.
        Assert.Equal(["Dana", "system", "system", "Dana", "system", "system", "Dana", "Dana", "Omar", "Lena", "Lena", "Dana"],
            timeline.Entries.Select(e => e.Actor));
        Assert.Equal(["original.csv", "original.csv", "original.csv", "corrected.csv", "corrected.csv", "corrected.csv",
                      "corrected.csv", "corrected.csv", "corrected.csv", null, null, "corrected.csv"],
            timeline.Entries.Select(e => e.Import?.FileName));

        // Previous and new values are in the entries (HIS-02, HIS-03).
        var ballot = timeline.Entries.Single(e => e.Action == "BallotChanged");
        Assert.Equal(("Y", "A"), (ballot.OldValue!.Value.GetProperty("ballot").GetString(), ballot.NewValue!.Value.GetProperty("ballot").GetString()));
        var position = timeline.Entries.Single(e => e.Action == "PositionChanged");
        Assert.Equal((4, 2), (position.OldValue!.Value.GetProperty("weight").GetInt32(), position.NewValue!.Value.GetProperty("weight").GetInt32()));
    }

    [Fact]
    public async Task Swagger_tells_how_to_set_the_first_position()
    {
        await using var api = await TestApi.StartAsync(fixture, environment: "Development");
        using var client = api.Client(user: null);

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var put = doc.RootElement.GetProperty("paths").GetProperty("/api/votes/{id}/position").GetProperty("put");

        Assert.Equal("Set the stance and weight of a vote (Admin only)", put.GetProperty("summary").GetString());
        // Line endings follow the checkout (CRLF on Windows with autocrlf).
        Assert.Contains("For the FIRST position of a vote\n(position is null), send expectedVersion 0",
            put.GetProperty("description").GetString()!.ReplaceLineEndings("\n"));
    }

    // ---- Content type of the older controllers ----

    [Fact]
    public async Task Import_errors_are_problem_json()
    {
        await using var api = await TestApi.StartAsync(fixture);
        using var client = api.Client();

        await ProblemAsync(await client.GetAsync("/api/imports/999999"), HttpStatusCode.NotFound);
        await ProblemAsync(await client.GetAsync("/api/imports/999999/report"), HttpStatusCode.NotFound);
        await ProblemAsync(await client.PostAsync("/api/imports", new MultipartFormDataContent()), HttpStatusCode.BadRequest);
    }
}
