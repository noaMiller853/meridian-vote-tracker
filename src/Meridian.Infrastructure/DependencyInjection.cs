using Meridian.Infrastructure.Files;
using Meridian.Infrastructure.Imports;
using Meridian.Infrastructure.Persistence;
using Meridian.Infrastructure.Review;
using Meridian.Infrastructure.Startup;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

        services.Configure<FileStoreOptions>(configuration.GetSection("FileStore"));
        services.AddSingleton<IFileStore, LocalFileStore>();

        services.Configure<ImportOptions>(configuration.GetSection("Imports"));
        services.AddSingleton<ImportSignal>();
        services.AddScoped<ImportService>();
        services.AddScoped<IImportProcessor, VoteImportProcessor>();
        services.AddHostedService<ImportWorker>();

        services.AddScoped<ConflictReader>();
        services.AddScoped<ConflictDecisionService>();

        return services;
    }
}
