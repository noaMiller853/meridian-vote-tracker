using Meridian.Api.Http;
using Meridian.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// One error format for the whole API (ProblemDetails), with the correlation id in every error.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.GetCorrelationId());
builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = FieldErrors.InvalidModelState);
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<UserHeaderSecurityScheme>());
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<CurrentUser>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<CorrelationIdMiddleware>();
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
