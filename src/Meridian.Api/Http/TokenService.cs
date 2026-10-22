using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Meridian.Api.Http;

/// <summary>Issues and reads the signed tokens of the Jwt mode (HS256). The role is not in the token: it is read from the database on every request.</summary>
public sealed class TokenService(IOptions<AuthOptions> options)
{
    private readonly JsonWebTokenHandler _handler = new();

    private SymmetricSecurityKey Key => new(Encoding.UTF8.GetBytes(options.Value.JwtKey));

    public (string Token, DateTimeOffset ExpiresAt) Issue(string userName, long userId, TimeSpan? lifetime = null)
    {
        var settings = options.Value;
        var life = lifetime ?? TimeSpan.FromMinutes(settings.TokenMinutes);
        var now = DateTime.UtcNow;
        var expires = now.Add(life);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            Subject = new ClaimsIdentity([new Claim("sub", userId.ToString()), new Claim("name", userName)]),
            NotBefore = life < TimeSpan.Zero ? expires.AddMinutes(-1) : now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
        };
        return (_handler.CreateToken(descriptor), new DateTimeOffset(expires, TimeSpan.Zero));
    }

    /// <summary>The user name in a valid, unexpired token signed by this server; otherwise null.</summary>
    public async Task<string?> ReadUserNameAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var settings = options.Value;
        try
        {
            var result = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = settings.Issuer,
                ValidateAudience = true,
                ValidAudience = settings.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = Key,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            });
            return result.IsValid ? result.ClaimsIdentity.FindFirst("name")?.Value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
