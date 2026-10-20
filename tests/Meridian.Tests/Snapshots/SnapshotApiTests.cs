using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meridian.Api.Controllers;
using Meridian.Infrastructure.Snapshots;
using Meridian.Tests.Imports;
using Meridian.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Meridian.Tests.Snapshots;

/// <summary>Spec 7.6 (SNP-01..SNP-05) and POS-04 through the API, against a real PostgreSQL.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SnapshotApiTests(PostgresFixture fixture)
{
    private const string Header = "assembly_vote_id,session,vote_date,resolution_title,country_code,country_name,ballot,published_at";

    private static string Csv(params string[] rows) => string.Join("\n", [Header, .. rows]) + "\n";

    private static string Row(string vote, string date, string title, string code, string ballot) =>
        $"{vote},79,{date},{title},{code},Name,{ballot},2024-04-02T09:00:00Z";

    private static async Task<Dictionary<string, long>> VoteIdsAsync(TestApi api)
    {
        await using var db = api.Db();
        return await db.Votes.ToDictionaryAsync(v => v.AssemblyVoteId, v => v.Id);
    }

    private static async Task SetPositionAsync(TestApi api, long voteId, string stance, int weight)
    {
        using var client = api.Client("Lena");
        var current = (await client.GetFromJsonAsync<JsonElement>($"/api/votes/{voteId}")).GetProperty("position");
        var version = current.ValueKind == JsonValueKind.Null ? 0 : current.GetProperty("version").GetInt32();
        var response = await client.PutAsJsonAsync($"/api/votes/{voteId}/position", new { stance, weight, expectedVersion = version });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> RequestAsync(TestApi api, object body, string user = "Dana")
    {
        using var client = api.Client(user);
        return await client.PostAsJsonAsync("/api/snapshots", body);
    }

    /// <summary>Requests a snapshot and waits until its job has finished.</summary>
    private static async Task<SnapshotDetails> CreateAsync(TestApi api, object body, string user = "Dana")
    {
        var response = await RequestAsync(api, body, user);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<SnapshotCreatedResponse>())!;
        return await WaitAsync(api, created.SnapshotId);
    }

    private static async Task<SnapshotDetails> WaitAsync(TestApi api, long id)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var details = await api.GetAsync<SnapshotDetails>($"/api/snapshots/{id}");
            if (details.Snapshot.JobStatus is "Succeeded" or "Failed")
                return details;
            if (watch.Elapsed > TimeSpan.FromSeconds(60))
                throw new TimeoutException($"Snapshot {id}: {details.Snapshot.JobStatus} {details.Snapshot.Phase}.");
            await Task.Delay(100);
        }
    }

    private static Task<CountryBreakdown> CountryAsync(TestApi api, long id, string code) =>
        api.GetAsync<CountryBreakdown>($"/api/snapshots/{id}/countries/{code}?take=500");

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<HttpResponseMessage> PublishAsync(TestApi api, long id, bool? replace = null, string user = "Lena")
    {
        using var client = api.Client(user);
        return await client.PutAsJsonAsync($"/api/snapshots/{id}/publish", new { replace });
    }

    // ---- The formula and the rules left open on purpose (spec 7.6, DECISIONS §6) ----

    // V1 weight 4 and V2 weight 2, the institute wants For on both (the spec example).
    private static readonly string TwoVotes = Csv(
        Row("MA/2024/001", "2024-01-10", "V1", "BRA", "Y"), Row("MA/2024/002", "2024-02-10", "V2", "BRA", "N"),
        Row("MA/2024/001", "2024-01-10", "V1", "IND", "Y"), Row("MA/2024/002", "2024-02-10", "V2", "IND", "Y"),
        Row("MA/2024/001", "2024-01-10", "V1", "FRA", "A"), Row("MA/2024/002", "2024-02-10", "V2", "FRA", "X"),
        Row("MA/2024/001", "2024-01-10", "V1", "JPN", "N"));                                    // JPN has no ballot on V2

    [Fact]
    public async Task Spec_example_scores_4_of_6_and_the_rules_are_shown()
    {
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("two.csv", TwoVotes);
        var ids = await VoteIdsAsync(api);
        await SetPositionAsync(api, ids["MA/2024/001"], "For", 4);
        await SetPositionAsync(api, ids["MA/2024/002"], "For", 2);

        var snapshot = await CreateAsync(api, new { name = "Q1", dateFrom = "2024-01-01", dateTo = "2024-03-31" });

        Assert.Equal(("Saved", "Succeeded", "2024-Q1", 2, 0), (snapshot.Snapshot.Status, snapshot.Snapshot.JobStatus,
            snapshot.Snapshot.Quarter, snapshot.IncludedVotes, snapshot.ExcludedVoteCount));
        var bra = snapshot.Ranking.Single(c => c.CountryCode == "BRA");
        Assert.Equal((66.666667m, "66.7", 6, 4), (bra.Score, bra.ScoreText, bra.CountedWeight, bra.MatchedWeight));

        // IND 100 (rank 1), BRA 66.7 (2), JPN 0 (3); FRA abstained and was absent: no counted vote, no score, no rank.
        Assert.Equal(["IND", "BRA", "JPN"], snapshot.Ranking.Where(c => c.Rank is not null).Select(c => c.CountryCode));
        Assert.Equal(["1 of 3", "2 of 3", "3 of 3"], snapshot.Ranking.Where(c => c.Rank is not null).Select(c => c.RankText));
        var fra = snapshot.Ranking.Single(c => c.CountryCode == "FRA");
        Assert.Equal(((decimal?)null, "n/a", (int?)null, "n/a", 0), (fra.Score, fra.ScoreText, fra.Rank, fra.RankText, fra.CountedWeight));
        Assert.Equal((0, 0, 1, 1), (fra.Votes.Matched, fra.Votes.NotMatched, fra.Votes.Abstained, fra.Votes.Absent));
        var jpn = snapshot.Ranking.Single(c => c.CountryCode == "JPN");
        Assert.Equal((0m, 4, 0, 1, 1), (jpn.Score!.Value, jpn.CountedWeight, jpn.MatchedWeight, jpn.Votes.NotMatched, jpn.Votes.NoBallot));
        // Every country that was a member on a vote date is listed (150 in the seed, minus NZL, which left in 2021);
        // those without any ballot have no score.
        Assert.Equal(149, snapshot.Ranking.Count);
        Assert.DoesNotContain(snapshot.Ranking, c => c.CountryCode == "NZL");
        Assert.Equal(3, snapshot.RankedCountries);

        // The rules chosen for what is open on purpose are part of the snapshot (spec 7.6).
        Assert.Contains(snapshot.Rules.Rules, r => r.StartsWith("Abstention (A): not counted"));
        Assert.Contains(snapshot.Rules.Rules, r => r.StartsWith("Absence (X): not counted"));
        Assert.Contains(snapshot.Rules.Rules, r => r.Contains("was not a member: not counted"));
        Assert.Contains(snapshot.Rules.Rules, r => r.Contains("no counted vote has no score (n/a) and no rank"));

        // SNP-03: the contribution of each vote to BRA's score.
        var breakdown = await CountryAsync(api, snapshot.Snapshot.Id, "bra");
        Assert.Equal(
            [("MA/2024/001", "Y", "Matched", true, true, 4), ("MA/2024/002", "N", "NotMatched", true, false, 2)],
            breakdown.Items.Select(i => (i.AssemblyVoteId, i.Ballot!, i.Outcome, i.Counted, i.Matched, i.Weight)));
        var fraBreakdown = await CountryAsync(api, snapshot.Snapshot.Id, "FRA");
        Assert.Equal([("A", "Abstained", false), ("X", "Absent", false)], fraBreakdown.Items.Select(i => (i.Ballot!, i.Outcome, i.Counted)));
        var jpnV2 = (await CountryAsync(api, snapshot.Snapshot.Id, "JPN")).Items[1];
        Assert.Equal(((string?)null, "NoBallot", false), (jpnV2.Ballot, jpnV2.Outcome, jpnV2.Counted));
    }

    [Fact]
    public async Task Votes_when_a_country_was_not_a_member_do_not_count_and_a_country_never_a_member_is_not_listed()
    {
        // Iceland left on 2019-12-31 and came back on 2022-01-01; New Zealand left on 2021-06-30.
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("isl.csv", Csv(
            Row("MA/2020/001", "2020-05-01", "Gap", "FRA", "Y"),     // ISL not a member, NZL a member without a ballot
            Row("MA/2020/001", "2020-05-01", "Gap", "NZL", "Y"),
            Row("MA/2024/001", "2024-01-10", "Back", "ISL", "Y"),
            Row("MA/2024/001", "2024-01-10", "Back", "FRA", "N")));
        var ids = await VoteIdsAsync(api);
        await SetPositionAsync(api, ids["MA/2020/001"], "For", 5);
        await SetPositionAsync(api, ids["MA/2024/001"], "For", 3);

        var all = await CreateAsync(api, new { name = "Both", assemblyVoteIds = new[] { "MA/2020/001", "MA/2024/001" } });
        var isl = await CountryAsync(api, all.Snapshot.Id, "ISL");
        Assert.Equal([("MA/2020/001", "NotAMember", false), ("MA/2024/001", "Matched", true)],
            isl.Items.Select(i => (i.AssemblyVoteId, i.Outcome, i.Counted)));
        Assert.Equal((100m, 3, 1), (isl.Country.Score!.Value, isl.Country.CountedWeight, isl.Country.Votes.NotAMember));
        Assert.Equal("2024-Q1", all.Snapshot.Quarter); // the latest included vote

        // Only the 2024 vote: NZL was not a member on that date, so it is not listed.
        var recent = await CreateAsync(api, new { name = "2024", dateFrom = "2024-01-01" });
        Assert.DoesNotContain(recent.Ranking, c => c.CountryCode == "NZL");
        Assert.Contains(all.Ranking, c => c.CountryCode == "NZL");
        await ProblemAsync(await api.Client().GetAsync($"/api/snapshots/{recent.Snapshot.Id}/countries/NZL"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Filter_and_list_are_a_union()
    {
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("two.csv", TwoVotes);
        await api.ImportAsync("later.csv", Csv(Row("MA/2024/050", "2024-06-01", "Later", "BRA", "Y"), Row("MA/2024/051", "2024-07-01", "Out", "BRA", "Y")));
        foreach (var id in (await VoteIdsAsync(api)).Values)
            await SetPositionAsync(api, id, "For", 1);

        var snapshot = await CreateAsync(api, new { name = "Union", dateTo = "2024-03-31", assemblyVoteIds = new[] { "MA/2024/050" } });

        var votes = await api.GetAsync<SnapshotVotePage>($"/api/snapshots/{snapshot.Snapshot.Id}/votes");
        Assert.Equal(["MA/2024/001", "MA/2024/002", "MA/2024/050"], votes.Items.OrderBy(v => v.Ordinal).Select(v => v.AssemblyVoteId));
        Assert.Equal("2024-Q2", snapshot.Snapshot.Quarter);
    }

    // ---- The frozen copy (SNP-04, storage choice in DECISIONS §6) ----

    [Fact]
    public async Task Character_i_belongs_to_the_vote_with_ordinal_i_also_after_the_votes_are_edited()
    {
        await using var api = await TestApi.StartAsync(fixture);
        // Five votes, written out of date order; BRA votes Y, N, A, X, Y in date order.
        var votes = new[] { ("MA/2024/005", "2024-03-05", "Y"), ("MA/2024/002", "2024-01-20", "N"), ("MA/2024/004", "2024-02-28", "Y"),
                            ("MA/2024/001", "2024-01-05", "Y"), ("MA/2024/003", "2024-02-01", "A") };
        await api.ImportAsync("five.csv", Csv(
            [.. votes.Select(v => Row(v.Item1, v.Item2, "Title " + v.Item1[^3..], "BRA", v.Item1 == "MA/2024/004" ? "X" : v.Item3)),
             .. votes.Select(v => Row(v.Item1, v.Item2, "Title " + v.Item1[^3..], "IND", "Y"))]));
        var ids = await VoteIdsAsync(api);
        foreach (var (voteId, weight) in new[] { ("MA/2024/001", 1), ("MA/2024/002", 2), ("MA/2024/003", 3), ("MA/2024/004", 4), ("MA/2024/005", 5) })
            await SetPositionAsync(api, ids[voteId], "For", weight);

        var snapshot = await CreateAsync(api, new { name = "Ordinals", session = 79 });
        var expected = new[]
        {
            (0, "MA/2024/001", "Title 001", "Y", 1), (1, "MA/2024/002", "Title 002", "N", 2), (2, "MA/2024/003", "Title 003", "A", 3),
            (3, "MA/2024/004", "Title 004", "X", 4), (4, "MA/2024/005", "Title 005", "Y", 5),
        };
        async Task CheckAsync()
        {
            var bra = await CountryAsync(api, snapshot.Snapshot.Id, "BRA");
            Assert.Equal(expected, bra.Items.Select(i => (i.Ordinal, i.AssemblyVoteId, i.Title, i.Ballot!, i.Weight)));
            await using var db = api.Db();
            var stored = await db.SnapshotScores.Where(s => s.SnapshotId == snapshot.Snapshot.Id && s.CountryCode == "BRA")
                .Select(s => new { s.Ballots, s.Outcomes }).SingleAsync();
            Assert.Equal(("YNAXY", "MDAXM"), (stored.Ballots, stored.Outcomes));
            var ordinals = await db.SnapshotVotes.Where(v => v.SnapshotId == snapshot.Snapshot.Id).OrderBy(v => v.Ordinal)
                .Select(v => v.AssemblyVoteId).ToListAsync();
            Assert.Equal(expected.Select(e => e.Item2), ordinals);
        }
        await CheckAsync();

        // Edit the votes afterwards: a new title and a new BRA ballot through approved conflicts, a new weight and stance.
        await api.ImportAsync("fix.csv", Csv(Row("MA/2024/002", "2024-01-20", "Renamed", "BRA", "Y"), Row("MA/2024/002", "2024-01-20", "Renamed", "IND", "Y")));
        using (var dana = api.Client("Dana"))
        {
            await using var db = api.Db();
            foreach (var conflict in await db.Conflicts.Select(c => c.Id).ToListAsync())
                Assert.Equal(HttpStatusCode.OK, (await dana.PostAsJsonAsync($"/api/conflicts/{conflict}/decision", new { decision = "Approve" })).StatusCode);
        }
        await SetPositionAsync(api, ids["MA/2024/003"], "Against", 1);
        var vote = await api.GetAsync<JsonElement>($"/api/votes/{ids["MA/2024/002"]}");
        Assert.Equal("Renamed", vote.GetProperty("title").GetString());

        await CheckAsync();
    }

    [Fact]
    public async Task Saved_snapshot_does_not_change_after_a_conflict_approval_or_a_weight_change()
    {
        // Spec 7.2: Lena sets positions, Dana creates the snapshot, Lena publishes it; BRA then changes from Y to A.
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("original.csv", Csv(Row("101", "2024-01-11", "T101", "BRA", "Y"), Row("101", "2024-01-11", "T101", "IND", "N")));
        var id = (await VoteIdsAsync(api))["101"];
        await SetPositionAsync(api, id, "For", 4);
        var first = await CreateAsync(api, new { name = "Q1", assemblyVoteIds = new[] { "101" } });
        Assert.Equal(HttpStatusCode.OK, (await PublishAsync(api, first.Snapshot.Id)).StatusCode);

        using var client = api.Client();
        var before = await client.GetStringAsync($"/api/snapshots/{first.Snapshot.Id}/countries/BRA");
        var rankingBefore = JsonSerializer.Serialize((await api.GetAsync<SnapshotDetails>($"/api/snapshots/{first.Snapshot.Id}")).Ranking);

        await api.ImportAsync("corrected.csv", Csv(Row("101", "2024-01-11", "T101", "BRA", "A"), Row("101", "2024-01-11", "T101", "IND", "N")));
        await using (var db = api.Db())
        {
            var conflict = await db.Conflicts.Select(c => c.Id).SingleAsync();
            using var dana = api.Client("Dana");
            Assert.Equal(HttpStatusCode.OK, (await dana.PostAsJsonAsync($"/api/conflicts/{conflict}/decision", new { decision = "Approve" })).StatusCode);
        }
        await SetPositionAsync(api, id, "Against", 1);

        // The saved snapshot shows exactly the same numbers: BRA still For (Y), weight 4, 100.
        Assert.Equal(before, await client.GetStringAsync($"/api/snapshots/{first.Snapshot.Id}/countries/BRA"));
        Assert.Equal(rankingBefore, JsonSerializer.Serialize((await api.GetAsync<SnapshotDetails>($"/api/snapshots/{first.Snapshot.Id}")).Ranking));
        var bra = await CountryAsync(api, first.Snapshot.Id, "BRA");
        Assert.Equal(("Y", "For", 4, 100m), (bra.Items[0].Ballot!, bra.Items[0].DesiredOutcome, bra.Items[0].Weight, bra.Country.Score!.Value));

        // A new snapshot uses the new values: BRA abstained (A), so no score; IND N against Against now matches.
        var second = await CreateAsync(api, new { name = "Q1 again", assemblyVoteIds = new[] { "101" } });
        var braNow = await CountryAsync(api, second.Snapshot.Id, "BRA");
        Assert.Equal(("A", "Against", 1, (decimal?)null), (braNow.Items[0].Ballot!, braNow.Items[0].DesiredOutcome, braNow.Items[0].Weight, braNow.Country.Score));
        Assert.Equal(100m, second.Ranking.Single(c => c.CountryCode == "IND").Score);
    }

    // ---- POS-04 ----

    [Fact]
    public async Task Votes_without_a_position_block_the_snapshot_unless_their_exclusion_is_confirmed()
    {
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("two.csv", TwoVotes);
        var ids = await VoteIdsAsync(api);
        await SetPositionAsync(api, ids["MA/2024/001"], "For", 4);

        var problem = await ProblemAsync(await RequestAsync(api, new { name = "Q1", session = 79 }), HttpStatusCode.Conflict);
        Assert.Equal(SnapshotsController.VotesWithoutPositionType, problem.GetProperty("type").GetString());
        Assert.Equal(1, problem.GetProperty("votesWithoutPosition").GetProperty("count").GetInt32());
        Assert.Equal(["MA/2024/002"], problem.GetProperty("votesWithoutPosition").GetProperty("assemblyVoteIds").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("excludeVotesWithoutPosition", problem.GetProperty("detail").GetString());
        await using (var db = api.Db())
            Assert.False(await db.Snapshots.AnyAsync());

        var snapshot = await CreateAsync(api, new { name = "Q1", session = 79, excludeVotesWithoutPosition = true });

        Assert.Equal((1, 1, true), (snapshot.IncludedVotes, snapshot.ExcludedVoteCount, snapshot.ExcludeVotesWithoutPosition));
        var excluded = Assert.Single(snapshot.ExcludedVotes);
        Assert.Equal(("MA/2024/002", "No position: excluded as confirmed by the creator (POS-04)"), (excluded.AssemblyVoteId, excluded.Reason));
        Assert.Equal([1], (await CountryAsync(api, snapshot.Snapshot.Id, "BRA")).Items.Select(i => i.Ordinal + 1)); // only V1 counts
        Assert.Equal(100m, snapshot.Ranking.Single(c => c.CountryCode == "BRA").Score);
    }

    // ---- Publishing (SNP-05) ----

    [Fact]
    public async Task One_snapshot_is_published_per_quarter_and_replace_swaps_them()
    {
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("two.csv", TwoVotes);
        foreach (var id in (await VoteIdsAsync(api)).Values)
            await SetPositionAsync(api, id, "For", 2);
        var first = await CreateAsync(api, new { name = "First", session = 79 });
        var second = await CreateAsync(api, new { name = "Second", dateFrom = "2024-01-01" });

        var published = (await (await PublishAsync(api, first.Snapshot.Id)).Content.ReadFromJsonAsync<SnapshotDetails>())!;
        Assert.Equal(("Published", "2024-Q1", "Lena"), (published.Snapshot.Status, published.Snapshot.Quarter, published.Snapshot.PublishedBy));

        var taken = await ProblemAsync(await PublishAsync(api, second.Snapshot.Id), HttpStatusCode.Conflict);
        Assert.Equal(SnapshotsController.QuarterTakenType, taken.GetProperty("type").GetString());
        Assert.StartsWith($"Snapshot {first.Snapshot.Id} (\"First\") is already published for 2024-Q1, by Lena at ", taken.GetProperty("detail").GetString());
        Assert.Equal(first.Snapshot.Id, taken.GetProperty("published").GetProperty("id").GetInt64());
        await ProblemAsync(await PublishAsync(api, first.Snapshot.Id), HttpStatusCode.Conflict); // already published

        var replaced = (await (await PublishAsync(api, second.Snapshot.Id, replace: true)).Content.ReadFromJsonAsync<SnapshotDetails>())!;
        Assert.Equal(("Published", first.Snapshot.Id), (replaced.Snapshot.Status, replaced.ReplacedSnapshotId));
        var old = await api.GetAsync<SnapshotDetails>($"/api/snapshots/{first.Snapshot.Id}");
        Assert.Equal(("Saved", (string?)null), (old.Snapshot.Status, old.Snapshot.PublishedBy));

        await using var db = api.Db();
        var events = await db.AuditEvents.Where(e => e.Entity == "snapshot").OrderBy(e => e.Id)
            .Select(e => new { e.Action, e.EntityId, Actor = e.Actor.Name, e.NewValue }).ToListAsync();
        Assert.Equal(["SnapshotRequested", "SnapshotSaved", "SnapshotRequested", "SnapshotSaved", "SnapshotPublished", "SnapshotUnpublished", "SnapshotPublished"],
            events.Select(e => e.Action));
        Assert.Equal(["Dana", "system", "Dana", "system", "Lena", "Lena", "Lena"], events.Select(e => e.Actor));
        using var last = JsonDocument.Parse(events[^1].NewValue!);
        Assert.Equal(first.Snapshot.Id, last.RootElement.GetProperty("replacedSnapshotId").GetInt64());
    }

    [Fact]
    public async Task Two_publishes_of_the_same_quarter_at_once_give_one_200_and_one_409()
    {
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("two.csv", TwoVotes);
        foreach (var id in (await VoteIdsAsync(api)).Values)
            await SetPositionAsync(api, id, "For", 2);
        using var first = api.Client("Lena");
        using var second = api.Client("Lena");

        for (var round = 0; round < 5; round++)
        {
            var a = (await CreateAsync(api, new { name = $"A{round}", session = 79 })).Snapshot.Id;
            var b = (await CreateAsync(api, new { name = $"B{round}", session = 79 })).Snapshot.Id;
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<HttpResponseMessage> Send(HttpClient client, long id)
            {
                await go.Task;
                return await client.PutAsJsonAsync($"/api/snapshots/{id}/publish", new { replace = true });
            }
            var ta = Send(first, a);
            var tb = Send(second, b);
            go.SetResult();
            var responses = await Task.WhenAll(ta, tb);

            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
        }

        await using var db = api.Db();
        Assert.Equal(1, await db.Snapshots.CountAsync(s => s.Status == "Published"));
    }

    // ---- Roles, validation, the background job ----

    [Fact]
    public async Task Analysts_and_the_admin_create_only_the_admin_publishes()
    {
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("two.csv", TwoVotes);
        foreach (var id in (await VoteIdsAsync(api)).Values)
            await SetPositionAsync(api, id, "For", 2);

        var byOmar = await CreateAsync(api, new { name = "Omar", session = 79 }, user: "Omar");
        await CreateAsync(api, new { name = "Lena", session = 79 }, user: "Lena");
        Assert.Equal("Omar", byOmar.Snapshot.CreatedBy);
        await ProblemAsync(await RequestAsync(api, new { name = "x", session = 79 }, user: "system"), HttpStatusCode.Forbidden);
        using (var anonymous = api.Client(user: null))
            await ProblemAsync(await anonymous.PostAsJsonAsync("/api/snapshots", new { name = "x", session = 79 }), HttpStatusCode.Unauthorized);

        var analyst = await ProblemAsync(await PublishAsync(api, byOmar.Snapshot.Id, user: "Dana"), HttpStatusCode.Forbidden);
        Assert.Equal("Dana (Analyst) cannot publish snapshots. Only the admin can (SNP-05).", analyst.GetProperty("detail").GetString());
        await ProblemAsync(await PublishAsync(api, byOmar.Snapshot.Id, user: "Omar"), HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.OK, (await PublishAsync(api, byOmar.Snapshot.Id)).StatusCode);
    }

    [Theory]
    [InlineData("""{"session":79}""", "name")]
    [InlineData("""{"name":"x"}""", "assemblyVoteIds")]
    [InlineData("""{"name":"x","dateFrom":"2024-12-31","dateTo":"2024-01-01"}""", "dateFrom")]
    [InlineData("""{"name":"x","assemblyVoteIds":["MA/2024/001","MA/1999/404"]}""", "assemblyVoteIds")]
    [InlineData("""{"name":"x","session":12}""", "session")]                          // matches no vote
    [InlineData("""{"name":"x","dateFrom":"not a date"}""", "dateFrom")]
    public async Task Bad_selections_are_400_under_the_field(string json, string field)
    {
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("two.csv", TwoVotes);
        using var client = api.Client();

        var problem = await ProblemAsync(await client.PostAsync("/api/snapshots", new StringContent(json, System.Text.Encoding.UTF8, "application/json")),
            HttpStatusCode.BadRequest);

        Assert.Equal([field], problem.GetProperty("errors").EnumerateObject().Select(p => p.Name));
        await using var db = api.Db();
        Assert.False(await db.Snapshots.AnyAsync());
    }

    [Fact]
    public async Task Calculation_runs_as_a_background_job_with_status_and_progress()
    {
        await using var api = await TestApi.StartAsync(fixture);
        await api.ImportAsync("two.csv", TwoVotes);
        foreach (var id in (await VoteIdsAsync(api)).Values)
            await SetPositionAsync(api, id, "For", 2);

        var response = await RequestAsync(api, new { name = "Job", session = 79 });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<SnapshotCreatedResponse>())!;
        Assert.Equal(2, created.SelectedVotes);
        Assert.EndsWith($"/api/snapshots/{created.SnapshotId}", response.Headers.Location!.ToString());
        var done = await WaitAsync(api, created.SnapshotId);

        Assert.Equal(("Saved", "Succeeded", (string?)null, 149, 149), (done.Snapshot.Status, done.Snapshot.JobStatus, done.Snapshot.Phase,
            done.Snapshot.Done, done.Snapshot.Total!.Value));
        Assert.Contains(await api.GetAsync<List<SnapshotSummary>>("/api/snapshots"), s => s.Id == created.SnapshotId);
        await using var db = api.Db();
        var job = await db.BackgroundJobs.SingleAsync(j => j.Id == created.JobId);
        Assert.Equal(("Snapshot", "Succeeded"), (job.Type, job.Status));
        Assert.NotNull(job.StartedAt);
        Assert.True(job.FinishedAt >= job.StartedAt);
    }

    [Fact]
    public async Task Snapshot_interrupted_by_a_restart_is_marked_failed()
    {
        await using var api = await TestApi.StartAsync(fixture);
        long snapshotId;
        await using (var connection = new NpgsqlConnection(api.ConnectionString))
        {
            await connection.OpenAsync();
            await using var cmd = new NpgsqlCommand("""
                WITH job AS (
                    INSERT INTO background_jobs (type, status, phase, done, started_at, created_at)
                    VALUES ('Snapshot', 'Running', 'Calculating', 10, now(), now()) RETURNING id)
                INSERT INTO snapshots (name, filter, rules, status, created_by_id, background_job_id)
                SELECT 'cut off', '{"assemblyVoteIds":[]}', '{"version":"1","rules":[]}', 'Calculating',
                       (SELECT id FROM users WHERE name = 'Dana'), id FROM job
                RETURNING id
                """, connection);
            snapshotId = (long)(await cmd.ExecuteScalarAsync())!;
        }

        var failed = await WaitAsync(api, snapshotId);

        Assert.Equal(("Failed", "Failed", SnapshotWorker.InterruptedMessage), (failed.Snapshot.Status, failed.Snapshot.JobStatus, failed.Snapshot.Error));
        await ProblemAsync(await PublishAsync(api, snapshotId), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Unknown_snapshot_is_a_404()
    {
        await using var api = await TestApi.StartAsync(fixture);
        using var client = api.Client();

        await ProblemAsync(await client.GetAsync("/api/snapshots/999999"), HttpStatusCode.NotFound);
        await ProblemAsync(await client.GetAsync("/api/snapshots/999999/countries/BRA"), HttpStatusCode.NotFound);
        await ProblemAsync(await client.GetAsync("/api/snapshots/999999/votes"), HttpStatusCode.NotFound);
        await ProblemAsync(await PublishAsync(api, 999_999), HttpStatusCode.NotFound);
    }
}
