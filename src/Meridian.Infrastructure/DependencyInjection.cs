using Meridian.Infrastructure.Persistence;
using Meridian.Infrastructure.Startup;
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

        services.AddDbContext<MeridianDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());

        services.Configure<DatabaseOptions>(configuration.GetSection("Database"));
        services.Configure<SeedOptions>(configuration.GetSection("Seed"));
        services.AddScoped<DatabaseInitializer>();
        services.AddHostedService<DatabaseInitializerService>();

        return services;
    }
}
