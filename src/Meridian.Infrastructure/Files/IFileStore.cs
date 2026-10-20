namespace Meridian.Infrastructure.Files;

/// <summary>
/// Where original uploaded files are kept (HIS-01). Local folder by default; Azure Blob Storage through configuration
/// (FileStore:Provider). Content-addressed: the same file is stored once.
/// </summary>
public interface IFileStore
{
    /// <summary>Streams the content to the store and computes its SHA-256 on the way, without buffering it in memory.</summary>
    Task<StoredFile> SaveAsync(Stream content, CancellationToken ct);

    Task<Stream> OpenReadAsync(string uri, CancellationToken ct);
}

public sealed record StoredFile(string Uri, string Sha256, long Length);

/// <summary>Section "FileStore".</summary>
public sealed class FileStoreOptions
{
    public const string ProviderLocal = "Local";
    public const string ProviderAzureBlob = "AzureBlob";

    /// <summary>Local (default) or AzureBlob.</summary>
    public string Provider { get; set; } = ProviderLocal;

    /// <summary>Local: folder for uploaded files, relative to the content root.</summary>
    public string LocalRoot { get; set; } = "data/uploads";

    /// <summary>AzureBlob: the account's blob endpoint, for example https://{account}.blob.core.windows.net.</summary>
    public string? BlobServiceUri { get; set; }

    /// <summary>AzureBlob: the container, for example uploads.</summary>
    public string? Container { get; set; }
}

/// <summary>The store refused or could not be reached (for example an expired access token). Not a problem of the file.</summary>
public sealed class FileStoreUnavailableException(string message, Exception inner) : Exception(message, inner);
