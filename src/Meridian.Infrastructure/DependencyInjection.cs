using Meridian.Infrastructure.Cloud;
using Meridian.Infrastructure.Files;
using Meridian.Infrastructure.Imports;
using Meridian.Infrastructure.Persistence;
using Meridian.Infrastructure.Review;
using Meridian.Infrastructure.Snapshots;
using Meridian.Infrastructure.Votes;
using Meridian.Infrastructure.Startup;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Meridian.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Comes from user-secrets locally or the ConnectionStrings__Default environment variable; never from code.
        var connectionString = configuration.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "Connection string 'Default' is not configured. Set it with " +
                "'dotnet user-secrets set \"ConnectionStrings:Default\" \"...\"' in src/Meridian.Api, " +
                "or with the ConnectionStrings__Default environment variable.");

        // Raw SQL of the import worker (advisory lock, claim, progress, COPY) uses this data source. EF gets the connection
        // string instead: a data source instance is part of EF's internal service-provider key, so one per host would
        // make EF build a new internal provider for every host (seen with many test hosts in one process).
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddDbContext<MeridianDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());

        services.Configure<DatabaseOptions>(configuration.GetSection("Database"));
        services.Configure<SeedOptions>(configuration.GetSection("Seed"));
        services.AddScoped<DatabaseInitializer>();
        // Registered before the import worker: StartingAsync migrates and seeds before any worker starts.
        services.AddHostedService<DatabaseInitializerService>();

        // Every Azure client authenticates through this factory (a configured token per resource, else DefaultAzureCredential).
        services.AddSingleton<AzureCredentialFactory>();
        // A configured token that cannot be sent (for example a pasted placeholder) stops startup with the setting's name,
        // instead of failing the first upload with a 500.
        services.AddOptions<AzureTokenOptions>()
            .Bind(configuration.GetSection("Azure"))
            .Validate(o => AzureTokenOptions.IsUsable(o.StorageAccessToken),
                AzureTokenOptions.UnreadableMessage(AzureCredentialFactory.StorageAccessTokenSetting, "AZURE_STORAGE_ACCESS_TOKEN",
                    "https://storage.azure.com/"))
            .Validate(o => AzureTokenOptions.IsUsable(o.KeyVaultAccessToken),
                AzureTokenOptions.UnreadableMessage(AzureCredentialFactory.KeyVaultAccessTokenSetting, "Azure__KeyVaultAccessToken",
                    "https://vault.azure.net"))
            .ValidateOnStart();

        // Local (default) or AzureBlob; a wrong setting stops startup instead of failing the first upload.
        services.AddOptions<FileStoreOptions>()
            .Bind(configuration.GetSection("FileStore"))
            .Validate(o => o.Provider is FileStoreOptions.ProviderLocal or FileStoreOptions.ProviderAzureBlob,
                $"FileStore:Provider must be {FileStoreOptions.ProviderLocal} or {FileStoreOptions.ProviderAzureBlob}.")
            .Validate(o => o.Provider != FileStoreOptions.ProviderAzureBlob ||
                           Uri.TryCreate(o.BlobServiceUri, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http",
                "FileStore:Provider is AzureBlob, so FileStore:BlobServiceUri must be the account's blob endpoint, for example https://<account>.blob.core.windows.net.")
            .Validate(o => o.Provider != FileStoreOptions.ProviderAzureBlob || !string.IsNullOrWhiteSpace(o.Container),
                "FileStore:Provider is AzureBlob, so FileStore:Container must be set, for example uploads.")
            .ValidateOnStart();
        services.AddSingleton<IFileStore>(sp =>
            sp.GetRequiredService<IOptions<FileStoreOptions>>().Value.Provider == FileStoreOptions.ProviderAzureBlob
                ? ActivatorUtilities.CreateInstance<AzureBlobFileStore>(sp)
                : ActivatorUtilities.CreateInstance<LocalFileStore>(sp));

        services.Configure<ImportOptions>(configuration.GetSection("Imports"));
        services.AddSingleton<ImportSignal>();
        services.AddScoped<ImportService>();
        services.AddScoped<IImportProcessor, VoteImportProcessor>();
        services.AddHostedService<ImportWorker>();

        services.Configure<SnapshotOptions>(configuration.GetSection("Snapshots"));
        services.AddSingleton<SnapshotSignal>();
        services.AddHostedService<SnapshotWorker>();
        services.AddScoped<SnapshotReader>();
        services.AddScoped<SnapshotService>();

        services.AddScoped<ConflictReader>();
        services.AddScoped<ConflictDecisionService>();

        services.AddScoped<VoteReader>();
        services.AddScoped<PositionService>();

        return services;
    }
}
