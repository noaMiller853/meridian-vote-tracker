namespace Meridian.Api.Http;

/// <summary>
/// How the API identifies the caller. <c>Header</c> is the simplified identification of spec section 3 (the user switcher
/// sends X-User; no password) and is meant for development, tests and the local compose. <c>Jwt</c> is for anything that
/// is reachable by other people: the user signs in with a password at POST /api/auth/login and sends the returned token.
/// </summary>
public sealed class AuthOptions
{
    public const string ModeHeader = "Header";
    public const string ModeJwt = "Jwt";

    public string Mode { get; set; } = ModeHeader;

    /// <summary>Signing key of the tokens (at least 32 characters). Comes from the environment, never from code.</summary>
    public string JwtKey { get; set; } = "";

    public string Issuer { get; set; } = "meridian-api";
    public string Audience { get; set; } = "meridian";
    public int TokenMinutes { get; set; } = 480;

    /// <summary>Password per user name (Auth__Passwords__Dana ...). A user without a password cannot sign in.</summary>
    public Dictionary<string, string> Passwords { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsJwt => string.Equals(Mode, ModeJwt, StringComparison.OrdinalIgnoreCase);
    public bool IsHeader => string.Equals(Mode, ModeHeader, StringComparison.OrdinalIgnoreCase);
}
