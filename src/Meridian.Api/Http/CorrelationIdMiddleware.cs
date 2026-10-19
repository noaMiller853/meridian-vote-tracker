using System.Text.RegularExpressions;

namespace Meridian.Api.Http;

/// <summary>
/// Takes X-Correlation-Id from the UI (or creates one), echoes it in the response, and puts it in the logging scope of
/// the request. Imports store it on the background job and on every audit event, so it reaches the worker's logs too.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string Header = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var id = context.Request.Headers[Header].ToString();
        if (!Valid().IsMatch(id))
            id = Guid.NewGuid().ToString("N");

        context.Items[Header] = id;
        context.Response.Headers[Header] = id;
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
            await next(context);
    }

    // Only safe characters, so the value can go into logs and headers unchanged.
    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex Valid();
}

public static class CorrelationIdExtensions
{
    public static string GetCorrelationId(this HttpContext context) =>
        context.Items[CorrelationIdMiddleware.Header] as string ?? context.TraceIdentifier;
}
