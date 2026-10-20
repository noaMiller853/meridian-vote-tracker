using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace Meridian.Infrastructure.Cloud;

/// <summary>The Azure resources the API talks to with Microsoft Entra authentication. A token is issued for one of them.</summary>
public enum AzureResource { Storage, KeyVault }

/// <summary>
/// One place that decides how every Azure client authenticates. If configuration holds an access token for the client's
/// resource (local Docker runs, where there is no az login: a short-lived token pasted from the terminal), it is used as
/// a static token. Otherwise <see cref="DefaultAzureCredential"/> (managed identity, environment, Azure CLI ...).
/// A token is never in a committed file.
/// </summary>
public sealed class AzureCredentialFactory(IConfiguration configuration)
{
    public const string StorageAccessTokenSetting = "Azure:StorageAccessToken";
    public const string KeyVaultAccessTokenSetting = "Azure:KeyVaultAccessToken";

    private readonly Lazy<TokenCredential> _default = new(() => new DefaultAzureCredential());

    public static string SettingFor(AzureResource resource) => resource switch
    {
        AzureResource.Storage => StorageAccessTokenSetting,
        AzureResource.KeyVault => KeyVaultAccessTokenSetting,
        _ => throw new ArgumentOutOfRangeException(nameof(resource)),
    };

    public TokenCredential Create(AzureResource resource)
    {
        var token = configuration[SettingFor(resource)];
        return string.IsNullOrWhiteSpace(token) ? _default.Value : new StaticTokenCredential(token.Trim());
    }
}

/// <summary>
/// A token obtained elsewhere (for example <c>az account get-access-token</c> in Cloud Shell). It is not refreshed: when it
/// expires (about an hour), Azure answers 401 and the API reports the file store as unavailable until it is restarted
/// with a new token.
/// </summary>
public sealed partial class StaticTokenCredential(string token) : TokenCredential
{
    private readonly AccessToken _token = new(token, ExpiresOn(token));

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => _token;

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_token);

    /// <summary>The <c>exp</c> claim of the JWT; one hour from now when it cannot be read.</summary>
    public static DateTimeOffset ExpiresOn(string token) =>
        TryReadExpiry(token, out var expiresOn) ? expiresOn : DateTimeOffset.UtcNow.AddHours(1);

    /// <summary>
    /// True when the token is a JWT this credential can send: three base64url parts (so it is a valid header value) and a
    /// payload with an <c>exp</c> claim. A pasted placeholder such as "&lt;paste the token&gt;" is not.
    /// </summary>
    public static bool TryReadExpiry(string token, out DateTimeOffset expiresOn)
    {
        expiresOn = default;
        if (!JwtShape().IsMatch(token))
            return false;
        try
        {
            var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            if (!json.RootElement.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var seconds))
                return false;
            expiresOn = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]*$")]
    private static partial Regex JwtShape();
}

/// <summary>Section "Azure": the access tokens per resource, checked at startup (a wrong one stops the API).</summary>
public sealed class AzureTokenOptions
{
    public string? StorageAccessToken { get; set; }
    public string? KeyVaultAccessToken { get; set; }

    /// <summary>Empty means "not set" (DefaultAzureCredential is used); a value must be a readable token.</summary>
    public static bool IsUsable(string? token) =>
        string.IsNullOrWhiteSpace(token) || StaticTokenCredential.TryReadExpiry(token.Trim(), out _);

    public static string UnreadableMessage(string setting, string environmentVariable, string resource) =>
        $"{setting} (environment variable {environmentVariable}) is set but is not a readable access token: expected a JWT, " +
        $"three base64url parts with an exp claim. Paste the output of az account get-access-token --resource {resource} " +
        "--query accessToken -o tsv, or leave it empty to use DefaultAzureCredential.";
}
