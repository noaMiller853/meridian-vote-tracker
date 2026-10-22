using System.Security.Cryptography;
using System.Text;
using Meridian.Mcp;

var builder = WebApplication.CreateBuilder(args);

// The key that clients (Claude) must send. Without it the server refuses to start: an open MCP endpoint would let anyone
// act in Meridian as the service account.
var apiKey = builder.Configuration["Mcp:ApiKey"] ?? "";
if (apiKey.Length < 24)
    throw new InvalidOperationException("Mcp:ApiKey (environment variable Mcp__ApiKey) must be a random value of at least 24 characters.");

builder.Services.Configure<MeridianApiOptions>(builder.Configuration.GetSection("Meridian"));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<MeridianApi>();
builder.Services.AddMcpServer().WithHttpTransport().WithTools<MeridianTools>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Cache-Control"] = "no-store";

    if (context.Request.Path.StartsWithSegments("/health"))
    {
        await next();
        return;
    }

    var authorization = context.Request.Headers.Authorization.ToString();
    var supplied = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization["Bearer ".Length..].Trim() : "";
    var same = CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
    if (!same)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return;
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapMcp("/mcp");

app.Run();
