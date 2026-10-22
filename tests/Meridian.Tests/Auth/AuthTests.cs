using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Meridian.Api.Http;
using Meridian.Tests.Imports;
using Meridian.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Tests.Auth;

/// <summary>The Jwt mode: password sign-in, tokens, and that nothing else (X-User, bad tokens) gets in.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AuthTests(PostgresFixture fixture)
{
    private const string DanaPassword = "dana-test-password-1";

    private static readonly Dictionary<string, string> JwtSettings = new()
    {
        ["Auth:Mode"] = "Jwt",
        ["Auth:JwtKey"] = "test-signing-key-test-signing-key-0123456789",
        ["Auth:Passwords:Dana"] = DanaPassword,
        ["Auth:Passwords:Lena"] = "lena-test-password-1",
    };

    private sealed record LoginBody(string Token, DateTimeOffset ExpiresAt, UserBody User);

    private sealed record UserBody(string Name, string Role);

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string user, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { userName = user, password });

    [Fact]
    public async Task Signing_in_gives_a_token_that_opens_the_api_with_the_role_from_the_database()
    {
        await using var api = await TestApi.StartAsync(fixture, settings: JwtSettings);
        using var client = api.Client(user: null);

        var response = await LoginAsync(client, "Lena", "lena-test-password-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = (await response.Content.ReadFromJsonAsync<LoginBody>())!;
        Assert.Equal(("Lena", "Admin"), (login.User.Name, login.User.Role));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/imports")).StatusCode);
    }

    [Fact]
    public async Task Without_a_token_the_answer_is_401_and_no_user_names_are_revealed()
    {
        await using var api = await TestApi.StartAsync(fixture, settings: JwtSettings);
        using var client = api.Client(user: null);

        var response = await client.GetAsync("/api/imports");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Dana", body);
        Assert.DoesNotContain("Lena", body);
    }

    [Fact]
    public async Task The_X_User_header_is_ignored_in_Jwt_mode()
    {
        await using var api = await TestApi.StartAsync(fixture, settings: JwtSettings);
        using var client = api.Client("Lena");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/imports")).StatusCode);
    }

    [Theory]
    [InlineData("Dana", "wrong-password")]
    [InlineData("Nobody", "whatever-password")]
    [InlineData("Omar", "omar-has-no-password")]
    [InlineData("system", "whatever-password")]
    public async Task A_wrong_password_an_unknown_user_or_a_user_without_a_password_cannot_sign_in(string user, string password)
    {
        await using var api = await TestApi.StartAsync(fixture, settings: JwtSettings);
        using var client = api.Client(user: null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, user, password)).StatusCode);
    }

    [Fact]
    public async Task A_missing_password_is_a_validation_error_beside_the_field()
    {
        await using var api = await TestApi.StartAsync(fixture, settings: JwtSettings);
        using var client = api.Client(user: null);

        var response = await LoginAsync(client, "Dana", "");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("password", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_expired_or_a_tampered_token_is_refused()
    {
        await using var api = await TestApi.StartAsync(fixture, settings: JwtSettings);
        using var client = api.Client(user: null);
        var tokens = api.Factory.Services.GetRequiredService<TokenService>();

        var (expired, _) = tokens.Issue("Dana", 2, TimeSpan.FromMinutes(-10));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", expired);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/imports")).StatusCode);

        var (valid, _) = tokens.Issue("Dana", 2);
        var tampered = valid[..^3] + (valid.EndsWith("AAA") ? "BBB" : "AAA");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tampered);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/imports")).StatusCode);
    }

    [Fact]
    public async Task The_role_comes_from_the_database_not_from_the_token()
    {
        // Dana is an Analyst: publishing is the admin's, so the answer is 403 even with a perfectly valid token.
        await using var api = await TestApi.StartAsync(fixture, settings: JwtSettings);
        using var client = api.Client(user: null);
        var login = (await (await LoginAsync(client, "Dana", DanaPassword)).Content.ReadFromJsonAsync<LoginBody>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var response = await client.PutAsJsonAsync("/api/snapshots/1/publish", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_mode_endpoint_tells_the_front_end_which_identification_is_used()
    {
        await using var jwt = await TestApi.StartAsync(fixture, settings: JwtSettings);
        using var jwtClient = jwt.Client(user: null);
        Assert.Contains("Jwt", await jwtClient.GetStringAsync("/api/auth/mode"));

        await using var header = await TestApi.StartAsync(fixture);
        using var headerClient = header.Client(user: null);
        Assert.Contains("Header", await headerClient.GetStringAsync("/api/auth/mode"));
        Assert.Equal(HttpStatusCode.NotFound, (await LoginAsync(headerClient, "Dana", "x")).StatusCode);
    }

    [Fact]
    public async Task Production_refuses_to_start_with_the_password_less_header_mode()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => TestApi.StartAsync(fixture, environment: "Production"));
    }

    [Fact]
    public async Task Jwt_mode_refuses_to_start_with_a_short_key()
    {
        var settings = new Dictionary<string, string>(JwtSettings) { ["Auth:JwtKey"] = "too-short" };

        await Assert.ThrowsAnyAsync<Exception>(() => TestApi.StartAsync(fixture, settings: settings));
    }
}
