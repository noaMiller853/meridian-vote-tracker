using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Meridian.Infrastructure.Files;

/// <summary>Stores files as {root}/{sha256}.csv and addresses them as local://{sha256}.csv.</summary>
public sealed partial class LocalFileStore : IFileStore
{
    private const string Scheme = "local://";
    private const int BufferSize = 1 << 20;
    private readonly string _root;

    public LocalFileStore(IOptions<FileStoreOptions> options, IHostEnvironment environment)
    {
        _root = Path.GetFullPath(options.Value.LocalRoot, environment.ContentRootPath);
        Directory.CreateDirectory(_root);
    }

    public async Task<StoredFile> SaveAsync(Stream content, CancellationToken ct)
    {
        var temp = Path.Combine(_root, $".upload-{Guid.NewGuid():N}.tmp");
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
            var final = Path.Combine(_root, name);
            try
            {
                File.Move(temp, final);
            }
            catch (IOException) when (File.Exists(final))
            {
                // Same content already stored (earlier or concurrent upload of the same file).
            }

            return new StoredFile(Scheme + name, sha256, length);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public Task<Stream> OpenReadAsync(string uri, CancellationToken ct)
    {
        // Only names this store produced; never a path from outside.
        if (!uri.StartsWith(Scheme, StringComparison.Ordinal) || !StoredName().IsMatch(uri[Scheme.Length..]))
            throw new ArgumentException($"Not a local file store URI: {uri}", nameof(uri));

        Stream stream = new FileStream(Path.Combine(_root, uri[Scheme.Length..]), FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    [GeneratedRegex("^[0-9a-f]{64}\\.csv$")]
    private static partial Regex StoredName();
}
