using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Meridian.Mcp;

public sealed class MeridianApiOptions
{
    /// <summary>Where the Meridian API is (inside Render's private network: http://meridian-api:8080).</summary>
    public string ApiUrl { get; set; } = "http://localhost:5284";

    /// <summary>The account the tools act as. Use an Analyst: the Admin's actions (positions, publishing) are not offered on purpose.</summary>
    public string UserName { get; set; } = "Dana";
}

/// <summary>
/// Calls the Meridian API as one service account. Against a Jwt-mode API it signs in with the password of
/// <c>Auth:Passwords:&lt;UserName&gt;</c> (the same setting the API reads) and renews the token before it expires; against a
/// Header-mode API (local development) it sends X-User instead.
/// </summary>
public sealed class MeridianApi(IHttpClientFactory factory, IOptions<MeridianApiOptions> options, IConfiguration configuration)
{
    private const int MaxChars = 60_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires;
    private bool _headerMode;

    public Task<string> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, ct);

    public Task<string> PostAsync(string path, object body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, path, () => JsonContent.Create(body), ct);

    private async Task<string> SendAsync(HttpMethod method, string path, Func<HttpContent>? content, CancellationToken ct)
    {
        var client = factory.CreateClient();
        for (var attempt = 0; ; attempt++)
        {
            var (token, headerMode) = await CredentialsAsync(client, ct);
            using var request = new HttpRequestMessage(method, options.Value.ApiUrl.TrimEnd('/') + path);
            if (content is not null) request.Content = content();
            if (headerMode) request.Headers.Add("X-User", options.Value.UserName);
            else request.Headers.Authorization = new("Bearer", token);
            request.Headers.Add("X-Correlation-Id", Guid.NewGuid().ToString("N"));

            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && !headerMode && attempt == 0)
            {
                _token = null; // the token was refused (key rotated, restart): sign in again once
                continue;
            }

            var text = await response.Content.ReadAsStringAsync(ct);
            if (text.Length > MaxChars) text = text[..MaxChars] + "\n...(cut: ask for a narrower list)";
            return $"HTTP {(int)response.StatusCode}\n{text}";
        }
    }

    private async Task<(string? Token, bool HeaderMode)> CredentialsAsync(HttpClient client, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_headerMode) return (null, true);
            if (_token is not null && _expires > DateTimeOffset.UtcNow.AddMinutes(1)) return (_token, false);

            var settings = options.Value;
            var password = configuration[$"Auth:Passwords:{settings.UserName}"] ?? "";
            using var response = await client.PostAsJsonAsync(settings.ApiUrl.TrimEnd('/') + "/api/auth/login",
                new { userName = settings.UserName, password }, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _headerMode = true; // the API identifies users by X-User (development)
                return (null, true);
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"The API refused the sign-in of '{settings.UserName}' (HTTP {(int)response.StatusCode}). Check Auth__Passwords__{settings.UserName}.");

            var login = await response.Content.ReadFromJsonAsync<LoginBody>(ct) ?? throw new InvalidOperationException("Empty sign-in answer.");
            _token = login.Token;
            _expires = login.ExpiresAt;
            return (_token, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record LoginBody(string Token, DateTimeOffset ExpiresAt);
}
