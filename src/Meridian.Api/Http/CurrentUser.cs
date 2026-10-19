using Meridian.Infrastructure.Persistence;
using Meridian.Infrastructure.Startup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

    internal void Set(long id, string name, string role) => (Id, Name, Role) = (id, name, role);
}

/// <summary>
/// Simplified identification (spec section 3): the user switcher sends X-User with the user name. Every /api request
/// must name a known user; the internal 'system' user cannot be used. Roles are then checked per endpoint.
/// </summary>
public sealed class CurrentUserMiddleware(RequestDelegate next, ILogger<CurrentUserMiddleware> logger)
{
    public const string Header = "X-User";

    public async Task InvokeAsync(HttpContext context, MeridianDbContext db, CurrentUser current, IProblemDetailsService problems)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        var name = context.Request.Headers[Header].ToString();
        var user = name.Length == 0 ? null : await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Name == name, context.RequestAborted);

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
        using (logger.BeginScope(new Dictionary<string, object> { ["User"] = user.Name }))
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
