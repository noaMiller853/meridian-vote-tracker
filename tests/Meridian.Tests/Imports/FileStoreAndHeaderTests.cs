using System.Security.Cryptography;
using System.Text;
using Meridian.Domain.Ingestion;
using Meridian.Infrastructure.Files;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Meridian.Tests.Imports;

public sealed class HeaderRuleTests
{
    private static readonly string[] Appendix = [.. ExportColumns.All];

    [Fact]
    public void Appendix_header_is_valid_in_any_order()
    {
        Assert.True(ExportColumns.CheckHeader(Appendix).IsValid);
        Assert.True(ExportColumns.CheckHeader(Appendix.Reverse().ToArray()).IsValid);
    }

    [Fact]
    public void Missing_unknown_and_repeated_columns_are_all_reported()
    {
        var header = new[] { "assembly_vote_id", "session", "vote_date", "title", "country_code", "country_name", "ballot", "ballot", "notes" };

        var check = ExportColumns.CheckHeader(header);

        Assert.False(check.IsValid);
        Assert.Equal(["resolution_title", "published_at"], check.Missing);
        Assert.Equal(["title", "notes"], check.Unknown);
        Assert.Equal(["ballot"], check.Repeated);
        Assert.Equal(
            "The header is not the Assembly export format (missing column(s): resolution_title, published_at; " +
            "unknown column(s): title, notes; repeated column(s): ballot). No row was processed.",
            check.Message);
    }

    [Fact]
    public void Column_names_are_exact()
    {
        var header = Appendix.Select(c => c == "ballot" ? " Ballot" : c).ToArray();

        Assert.Equal([" Ballot"], ExportColumns.CheckHeader(header).Unknown);
    }
}

public sealed class LocalFileStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("meridian-store-").FullName;
    private readonly LocalFileStore _store;

    public LocalFileStoreTests() =>
        _store = new LocalFileStore(Options.Create(new FileStoreOptions { LocalRoot = _root }), new Env(_root));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Stores_by_sha256_and_reads_back_the_same_bytes()
    {
        var bytes = Encoding.UTF8.GetBytes("assembly_vote_id,session\nMA/2024/001,79\n");

        var stored = await _store.SaveAsync(new MemoryStream(bytes), CancellationToken.None);

        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.Equal(new StoredFile($"local://{sha}.csv", sha, bytes.Length), stored);
        await using var read = await _store.OpenReadAsync(stored.Uri, CancellationToken.None);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        Assert.Equal(bytes, copy.ToArray());
    }

    [Fact]
    public async Task Same_content_is_stored_once_and_no_temp_file_remains()
    {
        var bytes = Encoding.UTF8.GetBytes("same");

        var a = await _store.SaveAsync(new MemoryStream(bytes), CancellationToken.None);
        var b = await _store.SaveAsync(new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(a, b);
        Assert.Single(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData("local://../secrets.csv")]
    [InlineData("local://C:/Windows/win.ini")]
    [InlineData("file:///etc/passwd")]
    [InlineData("local://ABC.csv")]
    public async Task Only_uris_of_this_store_can_be_opened(string uri)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.OpenReadAsync(uri, CancellationToken.None));
    }

    private sealed class Env(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
