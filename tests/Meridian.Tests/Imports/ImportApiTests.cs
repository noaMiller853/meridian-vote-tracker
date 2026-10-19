using System.Net;
using System.Net.Http.Json;
using Meridian.Api.Controllers;
using Meridian.Tests.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Tests.Imports;

/// <summary>Upload, status and history through the real API, with the real 4a worker.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ImportApiTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Upload_returns_202_at_once_and_the_job_then_completes()
    {
        await using var api = await TestApi.StartAsync(fixture);
        using var client = api.Client("Dana");

        var response = await client.PostAsync("/api/imports", TestApi.File("sample.csv", TestApi.SampleCsv));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ImportCreatedResponse>())!;
        Assert.Equal("Queued", created.Status);
        Assert.EndsWith($"/api/imports/{created.ImportId}", response.Headers.Location!.ToString());

        var done = await api.WaitUntilFinishedAsync(created.ImportId);
        Assert.Equal(("Succeeded", 3, (int?)3, (string?)null), (done.Import.Status, done.Import.Done, done.Import.Total, done.Import.Phase));
        Assert.Equal(("Votes", "sample.csv", "Dana"), (done.Import.Kind, done.Import.Label, done.Import.UploadedBy));
    }

    [Fact]
    public async Task Upload_and_processing_are_audited_with_the_correlation_id()
    {
        await using var api = await TestApi.StartAsync(fixture);
        using var client = api.Client("Omar", correlationId: "ui-req-42");

        var response = await client.PostAsync("/api/imports", TestApi.File("sample.csv", TestApi.SampleCsv));
        var created = (await response.Content.ReadFromJsonAsync<ImportCreatedResponse>())!;
        await api.WaitUntilFinishedAsync(created.ImportId);

        Assert.Equal("ui-req-42", response.Headers.GetValues("X-Correlation-Id").Single());
        await using var db = api.Db();
        Assert.Equal("ui-req-42", (await db.BackgroundJobs.SingleAsync(j => j.Id == created.JobId)).CorrelationId);
        var audit = await db.AuditEvents.Where(a => a.ImportId == created.ImportId && a.Entity == "import").OrderBy(a => a.Id)
            .Select(a => new { a.Action, Actor = a.Actor.Name, a.CorrelationId }).ToListAsync();
        Assert.Equal(
            [new { Action = "Uploaded", Actor = "Omar", CorrelationId = (string?)"ui-req-42" },
             new { Action = "Processed", Actor = "system", CorrelationId = (string?)"ui-req-42" }],
            audit);
    }

    [Fact]
    public async Task Unsafe_correlation_id_is_replaced()
    {
        await using var api = await TestApi.StartAsync(fixture);
        using var client = api.Client("Dana", correlationId: "has spaces; and more");

        var response = await client.GetAsync("/api/imports");

        Assert.Matches("^[0-9a-f]{32}$", response.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized, "Send the X-User header. Known users: Dana, Omar, Lena.")]
    [InlineData("Mallory", HttpStatusCode.Unauthorized, "Unknown user 'Mallory'. Known users: Dana, Omar, Lena.")]
    [InlineData("system", HttpStatusCode.Forbidden, "The system user only acts for the application itself.")]
    public async Task Acting_user_is_required_and_checked(string? user, HttpStatusCode status, string detail)
    {
        await using var api = await TestApi.StartAsync(fixture);
        using var client = api.Client(user, correlationId: "auth-1");

        var response = await client.PostAsync("/api/imports", TestApi.File("sample.csv", TestApi.SampleCsv));

        Assert.Equal(status, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Equal(detail, problem.Detail);
        Assert.Equal("auth-1", problem.Extensions["correlationId"]!.ToString());
        await using var db = api.Db();
        Assert.False(await db.Imports.AnyAsync(i => i.Kind == "Votes"));
    }

    [Fact]
    public async Task Empty_file_is_rejected_at_once()
    {
        await using var api = await TestApi.StartAsync(fixture);
        using var client = api.Client();

        var response = await client.PostAsync("/api/imports", TestApi.File("empty.csv", ""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("No file", (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
    }

    [Fact]
    public async Task Wrong_header_fails_the_whole_file_with_the_reason()
    {
        await using var api = await TestApi.StartAsync(fixture);

        var created = await api.UploadAsync("wrong.csv", "assembly_vote_id,session,vote_date,title,country_code,country_name,ballot,published_at\nMA/2024/001,79,2024-01-12,T,FRA,France,Y,\n");
        var done = await api.WaitUntilFinishedAsync(created.ImportId);

        Assert.Equal("Failed", done.Import.Status);
        Assert.Equal(
            "The header is not the Assembly export format (missing column(s): resolution_title; unknown column(s): title). No row was processed.",
            done.Import.Error);
        Assert.Equal(done.Import.Error, done.Report!.Value.GetProperty("fileError").GetString());
        await using var db = api.Db();
        Assert.Equal(["Uploaded", "Failed"], await db.AuditEvents.Where(a => a.ImportId == created.ImportId).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync());
    }

    [Fact]
    public async Task Header_only_file_succeeds_with_zero_rows_and_a_bom_is_ignored()
    {
        await using var api = await TestApi.StartAsync(fixture);
        using var client = api.Client();
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes(TestApi.SampleCsv.Split('\n')[0])).ToArray();

        var response = await client.PostAsync("/api/imports", TestApi.File("bom.csv", withBom));
        var created = (await response.Content.ReadFromJsonAsync<ImportCreatedResponse>())!;
        var done = await api.WaitUntilFinishedAsync(created.ImportId);

        Assert.Equal(("Succeeded", 0), (done.Import.Status, done.Import.Done));
    }

    [Fact]
    public async Task Same_file_twice_gives_two_imports_and_one_stored_file()
    {
        await using var api = await TestApi.StartAsync(fixture);

        var first = await api.UploadAsync("q1.csv", TestApi.SampleCsv);
        var second = await api.UploadAsync("q1-again.csv", TestApi.SampleCsv, user: "Omar");
        var a = await api.WaitUntilFinishedAsync(first.ImportId);
        var b = await api.WaitUntilFinishedAsync(second.ImportId);

        Assert.NotEqual(first.ImportId, second.ImportId);
        Assert.Equal(a.Import.Sha256, b.Import.Sha256);
        Assert.Equal(("Succeeded", "Succeeded"), (a.Import.Status, b.Import.Status));
        Assert.Equal([$"{a.Import.Sha256}.csv"], Directory.GetFiles(api.UploadRoot).Select(Path.GetFileName));
    }

    [Fact]
    public async Task History_lists_newest_first_and_labels_the_seed_import()
    {
        await using var api = await TestApi.StartAsync(fixture);
        var created = await api.UploadAsync("sample.csv", TestApi.SampleCsv);
        await api.WaitUntilFinishedAsync(created.ImportId);
        using var client = api.Client();

        var history = (await client.GetFromJsonAsync<List<ImportSummary>>("/api/imports"))!;

        Assert.Equal(["sample.csv", "member_states.csv (seed)"], history.Select(i => i.Label));
        Assert.Equal(("MemberStates", "Succeeded", "system"), (history[1].Kind, history[1].Status, history[1].UploadedBy));
    }

    [Fact]
    public async Task Unknown_import_is_a_404_problem()
    {
        await using var api = await TestApi.StartAsync(fixture);
        using var client = api.Client();

        var response = await client.GetAsync("/api/imports/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Import not found", (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
    }
}
