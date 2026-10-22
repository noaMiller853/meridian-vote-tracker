using System.Threading.RateLimiting;
using Meridian.Api.Http;
using Meridian.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// One error format for the whole API (ProblemDetails), with the correlation id in every error.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.GetCorrelationId());
builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = FieldErrors.InvalidModelState);
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<UserHeaderSecurityScheme>());
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<CurrentUser>();

// Identification: Header (development, tests, local compose) or Jwt (anything other people can reach). Production must use Jwt:
// the X-User header has no password, so a Production host that accepted it would let anyone act as the admin.
builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection("Auth"))
    .Validate(o => o.IsHeader || o.IsJwt, "Auth:Mode must be Header or Jwt.")
    .Validate(o => !builder.Environment.IsProduction() || o.IsJwt,
        "Auth:Mode must be Jwt in Production (set Auth__Mode=Jwt): the X-User header has no password.")
    .Validate(o => !o.IsJwt || o.JwtKey.Length >= 32, "Auth:JwtKey (Auth__JwtKey) must be a random value of at least 32 characters.")
    .Validate(o => !o.IsJwt || o.Passwords.Values.Any(p => p.Length >= 12),
        "Jwt mode needs at least one password of 12 characters or more (Auth__Passwords__<User>).")
    .ValidateOnStart();
builder.Services.AddSingleton<TokenService>();

// Sign-in attempts are limited per client address (guessing passwords).
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.AddApplicationInsightsWhenConfigured();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseHsts();

// Headers for every response. The CSP is only for the API (JSON); the Swagger UI of Development needs scripts.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    if (context.Request.Path.StartsWithSegments("/api")) headers["Cache-Control"] = "no-store";
    if (!app.Environment.IsDevelopment()) headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
    await next();
});

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseRateLimiter();
app.UseMiddleware<CurrentUserMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    // Browser UI on top of the built-in document, Development only: /swagger. "Authorize" sets X-User.
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "Meridian API v1");
        options.EnablePersistAuthorization();
    });
}

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();

app.Run();

// Lets the integration tests start the API with WebApplicationFactory<Program>.
public partial class Program;
