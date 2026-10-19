using Meridian.Infrastructure.Startup;
using Meridian.Tests.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Tests.Startup;

/// <summary>Starts the real API (WebApplicationFactory) on an empty database.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ApiStartupTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Api_starts_on_a_fresh_database_and_seeds_it()
    {
        var cs = await fixture.CreateEmptyDatabaseAsync();
        await using var factory = Api(cs, SeedFiles.RealMemberStates);

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/weatherforecast");

        Assert.True(response.IsSuccessStatusCode);
        await using var db = PostgresFixture.CreateContext(cs);
        Assert.Equal(150, await db.Countries.CountAsync());
        Assert.Equal(151, await db.CountryMemberships.CountAsync());
        Assert.Equal(4, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Api_refuses_to_start_without_seed_setting_when_there_are_no_countries()
    {
        var cs = await fixture.CreateEmptyDatabaseAsync();
        await using var factory = Api(cs, memberStatesPath: "");

        var ex = Assert.Throws<DatabaseStartupException>(() => factory.CreateClient());

        Assert.Contains("no countries", ex.Message);
        Assert.Contains("Seed:MemberStatesPath", ex.Message);
        await using var db = PostgresFixture.CreateContext(cs);
        Assert.Equal(0, await db.Countries.CountAsync());
    }

    private static WebApplicationFactory<Program> Api(string connectionString, string memberStatesPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseEnvironment("Testing")
            .UseSetting("ConnectionStrings:Default", connectionString)
            .UseSetting("Database:MigrateOnStartup", "true")
            .UseSetting("Seed:MemberStatesPath", memberStatesPath));
}
