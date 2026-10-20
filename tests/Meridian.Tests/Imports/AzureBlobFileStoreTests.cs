using System.Security.Cryptography;
using System.Text;
using Azure.Storage.Blobs;
using Meridian.Infrastructure.Files;
using Testcontainers.Azurite;

namespace Meridian.Tests.Imports;

/// <summary>
/// The Azure Blob file store against Azurite (the storage emulator) in Docker. Azurite authenticates with its
/// development key, not Microsoft Entra, so the store is given a container client built from the emulator's connection
/// string; the Entra path is covered by the credential factory tests and by the live check.
/// </summary>
public sealed class AzureBlobFileStoreTests : IAsyncLifetime
{
    private readonly AzuriteContainer _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.35.0")
        .WithCommand("--skipApiVersionCheck") // the SDK can be ahead of the emulator's known API versions
        .Build();

    private readonly string _temp = Directory.CreateTempSubdirectory("meridian-blob-test-").FullName;
    private BlobContainerClient _container = null!;
    private AzureBlobFileStore _store = null!;

    public async Task InitializeAsync()
    {
        await _azurite.StartAsync();
        _container = new BlobServiceClient(_azurite.GetConnectionString()).GetBlobContainerClient("uploads");
        await _container.CreateAsync();
        _store = new AzureBlobFileStore(_container, _temp);
    }

    public async Task DisposeAsync()
    {
        await _azurite.DisposeAsync();
        Directory.Delete(_temp, recursive: true);
    }

    private static MemoryStream Csv(string text) => new(Encoding.UTF8.GetBytes(text));

    private async Task<List<string>> BlobNamesAsync()
    {
        var names = new List<string>();
        await foreach (var blob in _container.GetBlobsAsync())
            names.Add(blob.Name);
        return names;
    }

    [Fact]
    public async Task Saves_the_file_under_its_sha256_and_reads_it_back()
    {
        const string text = "assembly_vote_id,session\nMA/2024/001,79\n";
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        var stored = await _store.SaveAsync(Csv(text), CancellationToken.None);

        Assert.Equal((expected, (long)Encoding.UTF8.GetByteCount(text), $"azureblob://uploads/{expected}.csv"),
            (stored.Sha256, stored.Length, stored.Uri));
        Assert.Equal([$"{expected}.csv"], await BlobNamesAsync());
        await using var read = await _store.OpenReadAsync(stored.Uri, CancellationToken.None);
        Assert.Equal(text, await new StreamReader(read).ReadToEndAsync());
        var properties = await _container.GetBlobClient($"{expected}.csv").GetPropertiesAsync();
        Assert.Equal("text/csv", properties.Value.ContentType);
    }

    [Fact]
    public async Task The_same_file_is_stored_once_and_another_file_separately()
    {
        var first = await _store.SaveAsync(Csv("a,b\n1,2\n"), CancellationToken.None);
        var again = await _store.SaveAsync(Csv("a,b\n1,2\n"), CancellationToken.None);
        var other = await _store.SaveAsync(Csv("a,b\n3,4\n"), CancellationToken.None);

        Assert.Equal(first, again);
        Assert.NotEqual(first.Uri, other.Uri);
        Assert.Equal(2, (await BlobNamesAsync()).Count);
        Assert.Empty(Directory.EnumerateFiles(_temp)); // no temporary file is left behind
    }

    [Fact]
    public async Task A_large_file_streams_through_with_the_right_hash()
    {
        // 3 MB: several buffers of the store, and more than one block for the upload.
        var bytes = new byte[3 * 1024 * 1024 + 17];
        new Random(7).NextBytes(bytes);

        var stored = await _store.SaveAsync(new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal((Convert.ToHexStringLower(SHA256.HashData(bytes)), (long)bytes.Length), (stored.Sha256, stored.Length));
        await using var read = await _store.OpenReadAsync(stored.Uri, CancellationToken.None);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        Assert.Equal(bytes, copy.ToArray());
    }

    [Theory]
    [InlineData("local://0000000000000000000000000000000000000000000000000000000000000000.csv")]
    [InlineData("azureblob://other/0000000000000000000000000000000000000000000000000000000000000000.csv")]
    [InlineData("azureblob://uploads/../secret.csv")]
    [InlineData("azureblob://uploads/notahash.csv")]
    public async Task Only_names_this_store_produced_can_be_opened(string uri) =>
        await Assert.ThrowsAsync<ArgumentException>(() => _store.OpenReadAsync(uri, CancellationToken.None));
}
