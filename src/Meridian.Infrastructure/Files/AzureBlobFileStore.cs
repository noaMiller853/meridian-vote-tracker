using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Meridian.Infrastructure.Cloud;
using Microsoft.Extensions.Options;

namespace Meridian.Infrastructure.Files;

/// <summary>
/// Stores files as blobs {container}/{sha256}.csv and addresses them as azureblob://{container}/{sha256}.csv, so a stored
/// import is not tied to the account endpoint. The name is only known after the last byte, so the upload is first
/// streamed to a temporary local file while it is hashed (memory stays at one buffer), then uploaded with
/// If-None-Match: * to its final name; if the blob already exists it is the same content, stored once (DECISIONS-DETAIL §10).
/// </summary>
public sealed partial class AzureBlobFileStore : IFileStore
{
    private const string Scheme = "azureblob://";
    private const int BufferSize = 1 << 20;

    private readonly BlobContainerClient _container;
    private readonly string _tempFolder;

    public AzureBlobFileStore(IOptions<FileStoreOptions> options, AzureCredentialFactory credentials)
        : this(new BlobContainerClient(ContainerUri(options.Value), credentials.Create(AzureResource.Storage)),
            Path.Combine(Path.GetTempPath(), "meridian-blob-uploads"))
    {
    }

    /// <summary>For a container client built elsewhere (the Azurite tests use the emulator's key).</summary>
    public AzureBlobFileStore(BlobContainerClient container, string tempFolder)
    {
        _container = container;
        _tempFolder = tempFolder;
        Directory.CreateDirectory(_tempFolder);
    }

    public async Task<StoredFile> SaveAsync(Stream content, CancellationToken ct)
    {
        var temp = Path.Combine(_tempFolder, $".upload-{Guid.NewGuid():N}.tmp");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    length += read;
                }
            }

            var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            var name = $"{sha256}.csv";
            try
            {
                await using var file = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
                await _container.GetBlobClient(name).UploadAsync(file, new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders { ContentType = "text/csv" },
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                }, ct);
            }
            catch (RequestFailedException e) when (e.ErrorCode == BlobErrorCode.BlobAlreadyExists || e.Status == 412)
            {
                // Same content already stored (earlier or concurrent upload of the same file).
            }
            catch (RequestFailedException e)
            {
                throw Unavailable(e);
            }

            return new StoredFile($"{Scheme}{_container.Name}/{name}", sha256, length);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public async Task<Stream> OpenReadAsync(string uri, CancellationToken ct)
    {
        // Only names this store produced; never a name from outside.
        var prefix = $"{Scheme}{_container.Name}/";
        if (!uri.StartsWith(prefix, StringComparison.Ordinal) || !StoredName().IsMatch(uri[prefix.Length..]))
            throw new ArgumentException($"Not a blob file store URI for container '{_container.Name}': {uri}", nameof(uri));

        try
        {
            return await _container.GetBlobClient(uri[prefix.Length..]).OpenReadAsync(new BlobOpenReadOptions(allowModifications: false), ct);
        }
        catch (RequestFailedException e)
        {
            throw Unavailable(e);
        }
    }

    private static FileStoreUnavailableException Unavailable(RequestFailedException e) =>
        new($"Azure Blob Storage refused the request ({e.Status} {e.ErrorCode}). An expired access token gives 401, a missing " +
            "blob data role 403.", e);

    private static Uri ContainerUri(FileStoreOptions options) =>
        new($"{options.BlobServiceUri!.TrimEnd('/')}/{options.Container}");

    [GeneratedRegex("^[0-9a-f]{64}\\.csv$")]
    private static partial Regex StoredName();
}
