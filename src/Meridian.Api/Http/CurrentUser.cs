using Meridian.Infrastructure.Persistence;
using Meridian.Infrastructure.Startup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Meridian.Api.Http;

/// <summary>The acting user of the request, set by <see cref="CurrentUserMiddleware"/>.</summary>
public sealed class CurrentUser
{
    public long Id { get; private set; }
    public string Name { get; private set; } = "";
    public string Role { get; private set; } = "";

    /// <summary>Spec section 3: analysts decide conflicts; the admin may too.</summary>
    public bool CanDecideConflicts => Role is "Analyst" or "Admin";

    /// <summary>POS-01: only the admin sets positions and weights.</summary>
    public bool CanSetPositions => Role is "Admin";

    /// <summary>Spec section 3: an analyst creates snapshots (the admin may too); only the admin publishes (SNP-05).</summary>
    public bool CanCreateSnapshots => Role is "Analyst" or "Admin";

    public bool CanPublishSnapshots => Role is "Admin";

    internal void Set(long id, string name, string role) => (Id, Name, Role) = (id, name, role);
}

/// <summary>
/// Identification of the caller. Header mode (spec section 3, simplified): the user switcher sends X-User with the user
/// name. Jwt mode: the Bearer token from POST /api/auth/login names the user. In both modes every /api request (except
/// /api/auth/*) must name a known user, the internal 'system' user cannot be used, and the role always comes from the
/// database. Roles are then checked per endpoint.
/// </summary>
public sealed class CurrentUserMiddleware(RequestDelegate next, ILogger<CurrentUserMiddleware> logger)
{
    public const string Header = "X-User";

    public async Task InvokeAsync(HttpContext context, MeridianDbContext db, CurrentUser current, IProblemDetailsService problems,
        IOptions<AuthOptions> authOptions, TokenService tokens)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api") || path.StartsWithSegments("/api/auth"))
        {
            await next(context);
            return;
        }

        var jwt = authOptions.Value.IsJwt;
        string name;
        if (jwt)
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization["Bearer ".Length..].Trim() : "";
            name = await tokens.ReadUserNameAsync(token) ?? "";
        }
        else
        {
            name = context.Request.Headers[Header].ToString();
        }
        var user = name.Length == 0 ? null : await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Name == name, context.RequestAborted);

        if (user is null && jwt)
        {
            // Nothing about the known users is revealed here.
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await WriteProblemAsync(context, problems, StatusCodes.Status401Unauthorized, "Authentication required",
                "Send a valid Bearer token from POST /api/auth/login.");
            return;
        }

        if (user is null)
        {
            var known = await db.Users.Where(u => u.Name != DatabaseInitializer.SystemUserName).OrderBy(u => u.Id)
                .Select(u => u.Name).ToListAsync(context.RequestAborted);
            var detail = name.Length == 0 ? $"Send the {Header} header." : $"Unknown user '{name}'.";
            await WriteProblemAsync(context, problems, StatusCodes.Status401Unauthorized, "No acting user",
                $"{detail} Known users: {string.Join(", ", known)}.");
            return;
        }

        if (user.Name == DatabaseInitializer.SystemUserName)
        {
            await WriteProblemAsync(context, problems, StatusCodes.Status403Forbidden, "Not allowed",
                "The system user only acts for the application itself.");
            return;
        }

        current.Set(user.Id, user.Name, user.Role);
        using (logger.BeginScope("User:{User}", user.Name))
            await next(context);
    }

    private static async Task WriteProblemAsync(HttpContext context, IProblemDetailsService problems, int status, string title, string detail)
    {
        context.Response.StatusCode = status;
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail },
        });
    }
}
