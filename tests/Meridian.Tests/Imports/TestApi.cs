using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Meridian.Api.Controllers;
using Meridian.Infrastructure.Persistence;
using Meridian.Tests.Persistence;
using Meridian.Tests.Startup;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Tests.Imports;

/// <summary>The real API (with its import worker) on its own database and its own upload folder.</summary>
internal sealed class TestApi : IAsyncDisposable
{
    /// <summary>Appendix A header and sample rows.</summary>
    public const string SampleCsv = """
        assembly_vote_id,session,vote_date,resolution_title,country_code,country_name,ballot,published_at
        MA/2024/117,79,2024-03-12,"Maritime Safety, Navigation and Trade",FRA,France,Y,2024-03-14T09:00:00Z
        MA/2024/117,79,2024-03-12,"Maritime Safety, Navigation and Trade",BRA,Brazil,A,2024-03-14T09:00:00Z
        MA/2024/117,79,2024-03-12,"Maritime Safety, Navigation and Trade",JPN,Japan,N,2024-03-14T09:00:00Z
        """;

    private TestApi(WebApplicationFactory<Program> factory, string connectionString, string uploadRoot)
    {
        Factory = factory;
        ConnectionString = connectionString;
        UploadRoot = uploadRoot;
    }

    public WebApplicationFactory<Program> Factory { get; }
    public string ConnectionString { get; }
    public string UploadRoot { get; }

    /// <param name="connectionString">An existing database; a new empty one when null.</param>
    /// <param name="services">Replacements, for example a test <c>IImportProcessor</c>.</param>
    /// <param name="settings">Extra configuration, for example <c>Imports:CommandTimeoutSeconds</c>.</param>
    public static async Task<TestApi> StartAsync(PostgresFixture fixture, string? connectionString = null,
        Action<IServiceCollection>? services = null, string? uploadRoot = null, IDictionary<string, string>? settings = null,
        string environment = "Testing")
    {
        connectionString ??= await fixture.CreateEmptyDatabaseAsync();
        uploadRoot ??= Directory.CreateTempSubdirectory("meridian-uploads-").FullName;

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment)
                .UseSetting("ConnectionStrings:Default", connectionString)
                .UseSetting("Database:MigrateOnStartup", "true")
                .UseSetting("Seed:MemberStatesPath", SeedFiles.RealMemberStates)
                .UseSetting("FileStore:LocalRoot", uploadRoot)
                .UseSetting("Imports:PollSeconds", "1")
                .UseSetting("Imports:ProgressIntervalMs", "50")
                .ConfigureTestServices(s => services?.Invoke(s));
            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
                b.UseSetting(key, value);
        });

        factory.CreateClient().Dispose(); // starts the host: migrations, seed, worker
        return new TestApi(factory, connectionString, uploadRoot);
    }

    public HttpClient Client(string? user = "Dana", string? correlationId = null)
    {
        var client = Factory.CreateClient();
        if (user is not null) client.DefaultRequestHeaders.Add("X-User", user);
        if (correlationId is not null) client.DefaultRequestHeaders.Add("X-Correlation-Id", correlationId);
        return client;
    }

    public static MultipartFormDataContent File(string fileName, string content) => File(fileName, Encoding.UTF8.GetBytes(content));

    public static MultipartFormDataContent File(string fileName, byte[] content)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        return new MultipartFormDataContent { { file, "file", fileName } };
    }

    public async Task<ImportCreatedResponse> UploadAsync(string fileName, string content, string user = "Dana", string? correlationId = null)
    {
        using var client = Client(user, correlationId);
        var response = await client.PostAsync("/api/imports", File(fileName, content));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ImportCreatedResponse>())!;
    }

    /// <summary>Uploads a file from disk as a stream (for the large files).</summary>
    public async Task<ImportCreatedResponse> UploadPathAsync(string path, string user = "Dana")
    {
        using var client = Client(user);
        client.Timeout = TimeSpan.FromMinutes(5);
        await using var stream = System.IO.File.OpenRead(path);
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        var response = await client.PostAsync("/api/imports", new MultipartFormDataContent { { file, "file", Path.GetFileName(path) } });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ImportCreatedResponse>())!;
    }

    public async Task<T> GetAsync<T>(string url)
    {
        using var client = Client();
        return (await client.GetFromJsonAsync<T>(url))!;
    }

    /// <summary>Uploads and waits until the import has finished; fails the test if it did not succeed.</summary>
    public async Task<ImportDetails> ImportAsync(string fileName, string content, TimeSpan? timeout = null)
    {
        var created = await UploadAsync(fileName, content);
        var done = await WaitUntilFinishedAsync(created.ImportId, timeout);
        if (done.Import.Status != "Succeeded")
            throw new InvalidOperationException($"Import of {fileName} {done.Import.Status}: {done.Import.Error}");
        return done;
    }

    public async Task<ImportDetails> GetAsync(long importId)
    {
        using var client = Client();
        return (await client.GetFromJsonAsync<ImportDetails>($"/api/imports/{importId}"))!;
    }

    /// <summary>Polls the API like the UI does, until the condition holds.</summary>
    public async Task<ImportDetails> WaitForAsync(long importId, Func<ImportSummary, bool> condition, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var details = await GetAsync(importId);
            if (condition(details.Import))
                return details;
            if (watch.Elapsed > limit)
                throw new TimeoutException($"Import {importId} did not reach the expected state in {limit}: status {details.Import.Status}, " +
                                           $"phase {details.Import.Phase}, done {details.Import.Done}, error {details.Import.Error}.");
            await Task.Delay(100);
        }
    }

    public Task<ImportDetails> WaitUntilFinishedAsync(long importId, TimeSpan? timeout = null) =>
        WaitForAsync(importId, i => i.Status is "Succeeded" or "Failed", timeout);

    public MeridianDbContext Db() => PostgresFixture.CreateContext(ConnectionString);

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        try { Directory.Delete(UploadRoot, recursive: true); } catch (IOException) { }
    }
}
