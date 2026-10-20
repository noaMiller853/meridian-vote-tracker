using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Meridian.Api.Http;
using Meridian.Tests.Imports;
using Meridian.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;

namespace Meridian.Tests.Startup;

/// <summary>Application Insights is configuration only: without the connection string the API runs exactly as before.</summary>
[Collection(PostgresCollection.Name)]
public sealed class TelemetryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Without_the_setting_the_api_starts_health_works_and_no_telemetry_is_registered()
    {
        await using var api = await TestApi.StartAsync(fixture);
        using var client = api.Client(user: null);

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(api.Factory.Services.GetService<TracerProvider>());
    }

    [Fact]
    public async Task An_empty_setting_means_off()
    {
        // docker compose passes the variable empty when .env does not set it.
        await using var api = await TestApi.StartAsync(fixture, settings: new Dictionary<string, string> { [Telemetry.ConnectionStringSetting] = "" });
        using var client = api.Client(user: null);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Null(api.Factory.Services.GetService<TracerProvider>());
    }

    [Fact]
    public async Task The_request_activity_carries_the_correlation_id()
    {
        var requests = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = requests.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        await using var api = await TestApi.StartAsync(fixture);
        using var client = api.Client(user: "Dana", correlationId: "telemetry-test-7");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/votes?take=1")).StatusCode);

        // The request activity stops when the response is complete; give it a moment.
        for (var i = 0; i < 50 && !requests.Any(Tagged); i++)
            await Task.Delay(20);
        Assert.Contains(requests, Tagged);

        static bool Tagged(Activity a) => (a.GetTagItem(Telemetry.CorrelationIdName) as string) == "telemetry-test-7";
    }

    [Fact]
    public async Task The_log_scope_carries_the_correlation_id_as_a_property_and_as_text()
    {
        var scopes = new ScopeCapturingProvider();
        await using var api = await TestApi.StartAsync(fixture, services: s => s.AddSingleton<ILoggerProvider>(scopes));
        using var client = api.Client(user: "Dana", correlationId: "telemetry-test-8");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/votes?take=1")).StatusCode);

        // OpenTelemetry (IncludeScopes) reads the key/value pairs; the console prints ToString().
        var scope = Assert.Single(scopes.States, s => s.ToString() == "CorrelationId:telemetry-test-8");
        var pairs = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(scope);
        Assert.Contains(pairs, p => p.Key == Telemetry.CorrelationIdName && Equals(p.Value, "telemetry-test-8"));
    }

    /// <summary>Records the state of every log scope that is opened.</summary>
    private sealed class ScopeCapturingProvider : ILoggerProvider
    {
        public ConcurrentQueue<object> States { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose() { }

        private sealed class Logger(ScopeCapturingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                owner.States.Enqueue(state);
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => false;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
        }
    }
}
