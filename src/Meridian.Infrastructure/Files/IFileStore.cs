namespace Meridian.Infrastructure.Files;

/// <summary>
/// Where original uploaded files are kept (HIS-01). Local folder by default; Azure Blob Storage only through
/// configuration later (DECISIONS §10). Content-addressed: the same file is stored once.
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
    /// <summary>Folder for uploaded files, relative to the content root.</summary>
    public string LocalRoot { get; set; } = "data/uploads";
}
