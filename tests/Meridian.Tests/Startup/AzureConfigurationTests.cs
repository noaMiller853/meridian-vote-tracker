using System.Text;
using Azure.Core;
using Azure.Identity;
using Meridian.Infrastructure.Cloud;
using Meridian.Infrastructure.Files;
using Meridian.Tests.Imports;
using Meridian.Tests.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Meridian.Tests.Startup;

/// <summary>The credential factory (one token per resource) and the choice of file store by configuration.</summary>
public sealed class AzureCredentialFactoryTests
{
    private static AzureCredentialFactory Factory(params (string Key, string Value)[] settings) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value)).Build());

    private static string Jwt(long exp) =>
        "eyJhbGciOiJub25lIn0." + Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"exp":{{exp}}}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    [Fact]
    public async Task A_configured_storage_token_is_used_as_is_with_its_expiry()
    {
        var token = Jwt(1_900_000_000);
        var credential = Factory((AzureCredentialFactory.StorageAccessTokenSetting, token)).Create(AzureResource.Storage);

        var access = await credential.GetTokenAsync(new TokenRequestContext(["https://storage.azure.com/.default"]), CancellationToken.None);

        Assert.IsType<StaticTokenCredential>(credential);
        Assert.Equal((token, DateTimeOffset.FromUnixTimeSeconds(1_900_000_000)), (access.Token, access.ExpiresOn));
    }

    [Fact]
    public void Each_resource_has_its_own_token_otherwise_DefaultAzureCredential()
    {
        var factory = Factory((AzureCredentialFactory.StorageAccessTokenSetting, Jwt(1_900_000_000)));

        Assert.IsType<StaticTokenCredential>(factory.Create(AzureResource.Storage));
        Assert.IsType<DefaultAzureCredential>(factory.Create(AzureResource.KeyVault)); // a storage token is not used for Key Vault
        Assert.IsType<DefaultAzureCredential>(Factory().Create(AzureResource.Storage));
        Assert.IsType<DefaultAzureCredential>(Factory((AzureCredentialFactory.StorageAccessTokenSetting, " ")).Create(AzureResource.Storage));
        Assert.Equal(("Azure:StorageAccessToken", "Azure:KeyVaultAccessToken"),
            (AzureCredentialFactory.SettingFor(AzureResource.Storage), AzureCredentialFactory.SettingFor(AzureResource.KeyVault)));
    }

    [Theory]
    [InlineData("<paste the token>")]   // the placeholder from the README, pasted by mistake
    [InlineData("not-a-jwt")]
    [InlineData("a.b.c")]               // three parts, payload not JSON
    [InlineData("eyJhbGciOiJub25lIn0.eyJzdWIiOiJ4In0.sig")] // JSON payload without exp
    [InlineData("eyJh bGci.eyJ.sig")]   // a space: not a valid header value
    public void Unreadable_tokens_are_recognised(string token)
    {
        Assert.False(StaticTokenCredential.TryReadExpiry(token, out _));
        Assert.False(AzureTokenOptions.IsUsable(token));
    }

    [Fact]
    public void A_real_shaped_token_or_no_token_is_usable()
    {
        Assert.True(StaticTokenCredential.TryReadExpiry(Jwt(1_900_000_000), out var expiresOn));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_900_000_000), expiresOn);
        Assert.True(AzureTokenOptions.IsUsable(null));
        Assert.True(AzureTokenOptions.IsUsable(" "));
    }

    [Fact]
    public void A_token_that_is_not_a_jwt_is_assumed_to_last_an_hour() =>
        Assert.InRange(StaticTokenCredential.ExpiresOn("not-a-jwt"), DateTimeOffset.UtcNow.AddMinutes(59), DateTimeOffset.UtcNow.AddMinutes(61));
}

[Collection(PostgresCollection.Name)]
public sealed class FileStoreSelectionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Local_is_the_default()
    {
        await using var api = await TestApi.StartAsync(fixture);

        Assert.IsType<LocalFileStore>(api.Factory.Services.GetRequiredService<IFileStore>());
    }

    [Theory]
    [InlineData("AzureBlob", null, "uploads", "FileStore:BlobServiceUri")]
    [InlineData("AzureBlob", "https://account.blob.core.windows.net", null, "FileStore:Container")]
    [InlineData("S3", null, null, "FileStore:Provider must be Local or AzureBlob")]
    public async Task A_wrong_setting_stops_startup_with_a_clear_message(string provider, string? uri, string? container, string expected)
    {
        var settings = new Dictionary<string, string> { ["FileStore:Provider"] = provider };
        if (uri is not null) settings["FileStore:BlobServiceUri"] = uri;
        if (container is not null) settings["FileStore:Container"] = container;

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => TestApi.StartAsync(fixture, settings: settings));

        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("<paste the token>")]
    [InlineData("not-a-jwt")]
    public async Task An_unreadable_storage_token_stops_startup_naming_the_setting(string token)
    {
        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => TestApi.StartAsync(fixture, settings: new Dictionary<string, string>
        {
            ["FileStore:Provider"] = "AzureBlob",
            ["FileStore:BlobServiceUri"] = "https://example.blob.core.windows.net",
            ["FileStore:Container"] = "uploads",
            ["Azure:StorageAccessToken"] = token,
        }));

        Assert.Contains("Azure:StorageAccessToken (environment variable AZURE_STORAGE_ACCESS_TOKEN) is set but is not a readable access token", ex.Message);
        Assert.Contains("az account get-access-token --resource https://storage.azure.com/", ex.Message);
        Assert.DoesNotContain(token, ex.Message); // the value itself is never echoed
    }

    [Fact]
    public async Task A_readable_storage_token_starts_normally()
    {
        var jwt = "eyJhbGciOiJub25lIn0." + Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"exp":1900000000}""")).TrimEnd('=') + ".sig";

        await using var api = await TestApi.StartAsync(fixture, settings: new Dictionary<string, string>
        {
            ["FileStore:Provider"] = "AzureBlob",
            ["FileStore:BlobServiceUri"] = "https://example.blob.core.windows.net",
            ["FileStore:Container"] = "uploads",
            ["Azure:StorageAccessToken"] = jwt,
        });

        Assert.IsType<AzureBlobFileStore>(api.Factory.Services.GetRequiredService<IFileStore>());
    }

    [Fact]
    public async Task AzureBlob_is_chosen_by_configuration_without_contacting_Azure()
    {
        // Building the store creates no request; nothing is uploaded here.
        await using var api = await TestApi.StartAsync(fixture, settings: new Dictionary<string, string>
        {
            ["FileStore:Provider"] = "AzureBlob",
            ["FileStore:BlobServiceUri"] = "https://example.blob.core.windows.net",
            ["FileStore:Container"] = "uploads",
        });

        Assert.IsType<AzureBlobFileStore>(api.Factory.Services.GetRequiredService<IFileStore>());
    }
}
