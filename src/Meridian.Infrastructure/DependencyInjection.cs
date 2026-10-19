using Meridian.Infrastructure.Files;
using Meridian.Infrastructure.Imports;
using Meridian.Infrastructure.Persistence;
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

        // One data source for EF and for the raw SQL of the import worker (advisory lock, claim, progress, COPY).
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddDbContext<MeridianDbContext>((sp, options) => options
            .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
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
        services.AddScoped<IImportProcessor, HeaderAndCountProcessor>();
        services.AddHostedService<ImportWorker>();

        return services;
    }
}
