using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Logs;

namespace Meridian.Api.Http;

/// <summary>
/// Azure Application Insights through configuration only: on when APPLICATIONINSIGHTS_CONNECTION_STRING is set and not
/// empty (compose passes it from .env, empty by default), otherwise nothing is registered and logs stay on the console.
/// </summary>
public static class Telemetry
{
    public const string ConnectionStringSetting = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    /// <summary>The name of the correlation id on request telemetry and on logs (the log scope key), so one query finds both.</summary>
    public const string CorrelationIdName = "CorrelationId";

    public static WebApplicationBuilder AddApplicationInsightsWhenConfigured(this WebApplicationBuilder builder)
    {
        // An empty value must mean "off": the distro would throw at startup on an empty connection string.
        var connectionString = builder.Configuration[ConnectionStringSetting];
        if (string.IsNullOrWhiteSpace(connectionString))
            return builder;

        builder.Services.AddOpenTelemetry().UseAzureMonitor(options =>
        {
            options.ConnectionString = connectionString;
            // Every request is kept, so any one of them can be searched end to end by its correlation id. The volume is
            // small; the rate-limited sampler takes precedence over the ratio, so it is switched off explicitly.
            options.TracesPerSecond = null;
            options.SamplingRatio = 1.0F;
        });
        // Log scopes become log properties: CorrelationId (requests and background jobs), ImportId, SnapshotId, User.
        builder.Services.Configure<OpenTelemetryLoggerOptions>(options => options.IncludeScopes = true);
        return builder;
    }
}
