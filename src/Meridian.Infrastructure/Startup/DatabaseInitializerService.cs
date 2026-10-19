using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Meridian.Infrastructure.Startup;

/// <summary>
/// Runs <see cref="DatabaseInitializer"/> in StartingAsync, i.e. before any hosted service and before the web server
/// accepts requests. An exception here stops the host, so the API never runs on an unusable database.
/// </summary>
public sealed class DatabaseInitializerService(
    IServiceScopeFactory scopes,
    IHostEnvironment environment,
    IOptions<DatabaseOptions> database,
    IOptions<SeedOptions> seed) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var path = seed.Value.MemberStatesPath;
        var fullPath = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path, environment.ContentRootPath);

        await using var scope = scopes.CreateAsyncScope();
        var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        await initializer.InitializeAsync(database.Value.MigrateOnStartup, fullPath, cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
