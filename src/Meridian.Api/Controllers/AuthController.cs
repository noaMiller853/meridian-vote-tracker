using System.Security.Cryptography;
using System.Text;
using Meridian.Api.Http;
using Meridian.Infrastructure.Persistence;
using Meridian.Infrastructure.Startup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Meridian.Api.Controllers;

public sealed record AuthModeResponse(string Mode);

public sealed record LoginRequest(string? UserName, string? Password);

public sealed record LoginUser(string Name, string Role);

public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, LoginUser User);

/// <summary>Sign-in for the Jwt mode. Both endpoints are open (no token needed); everything else under /api needs the acting user.</summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(MeridianDbContext db, IOptions<AuthOptions> options, TokenService tokens) : ControllerBase
{
    /// <summary>Which identification this server uses, so the front end knows whether to show a sign-in form.</summary>
    [HttpGet("mode")]
    public ActionResult<AuthModeResponse> Mode() => new AuthModeResponse(options.Value.IsJwt ? AuthOptions.ModeJwt : AuthOptions.ModeHeader);

    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var auth = options.Value;
        if (!auth.IsJwt)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Sign-in is not enabled",
                detail: "This server identifies the user by the X-User header.");

        var name = request.UserName?.Trim() ?? "";
        var password = request.Password ?? "";
        if (name.Length == 0) ModelState.AddModelError("userName", "Enter the user name.");
        if (password.Length == 0) ModelState.AddModelError("password", "Enter the password.");
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var user = await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Name == name && u.Name != DatabaseInitializer.SystemUserName, ct);
        var expected = user is null
            ? null
            : auth.Passwords.FirstOrDefault(p => string.Equals(p.Key, user.Name, StringComparison.OrdinalIgnoreCase)).Value;

        // The comparison always runs, so an unknown user and a wrong password take the same time and give the same answer.
        var matches = PasswordsMatch(password, string.IsNullOrEmpty(expected) ? Guid.NewGuid().ToString("N") : expected);
        if (user is null || string.IsNullOrEmpty(expected) || !matches)
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign-in failed", detail: "Wrong user name or password.");

        var (token, expiresAt) = tokens.Issue(user.Name, user.Id);
        return new LoginResponse(token, expiresAt, new LoginUser(user.Name, user.Role));
    }

    private static bool PasswordsMatch(string supplied, string expected) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}
