using Meridian.Infrastructure.Persistence;
using Meridian.Infrastructure.Persistence.Entities;
using Meridian.Infrastructure.Startup;
using Meridian.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meridian.Tests.Startup;

/// <summary>Startup seeding against a real PostgreSQL. Every test starts from its own empty database.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ReferenceDataTests(PostgresFixture fixture) : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("meridian-seed-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public async Task Fresh_database_gets_the_real_seed_file_and_users()
    {
        var cs = await fixture.CreateEmptyDatabaseAsync();

        await Initialize(cs, SeedFiles.RealMemberStates);

        await using var db = PostgresFixture.CreateContext(cs);
        Assert.Equal(150, await db.Countries.CountAsync());
        Assert.Equal(151, await db.CountryMemberships.CountAsync());

        Assert.Equal(
            [(new DateOnly(1995, 1, 1), (DateOnly?)new DateOnly(2019, 12, 31)), (new DateOnly(2022, 1, 1), null)],
            await Periods(db, "ISL"));
        Assert.Equal([(new DateOnly(1995, 1, 1), (DateOnly?)new DateOnly(2021, 6, 30))], await Periods(db, "NZL"));

        Assert.Equal(
            ["Dana/Analyst", "Lena/Admin", "Omar/Analyst", "system/Admin"],
            (await db.Users.Select(u => u.Name + "/" + u.Role).ToListAsync()).Order());

        var import = await db.Imports.Include(i => i.UploadedBy).SingleAsync();
        Assert.Equal(Import.KindMemberStates, import.Kind);
        Assert.Equal("member_states.csv", import.FileName);
        Assert.Equal("system", import.UploadedBy.Name);
        Assert.Equal(151, import.NewCount);
        Assert.Equal(0, import.UnchangedCount);

        // One audit event per user, country and period added.
        Assert.Equal(3, await db.AuditEvents.CountAsync(a => a.Entity == "user"));
        Assert.Equal(150, await db.AuditEvents.CountAsync(a => a.Entity == "country" && a.ImportId == import.Id));
        Assert.Equal(151, await db.AuditEvents.CountAsync(a => a.Entity == "country_membership" && a.ImportId == import.Id));
    }

    [Fact]
    public async Task Loading_twice_changes_nothing()
    {
        var cs = await fixture.CreateEmptyDatabaseAsync();

        await Initialize(cs, SeedFiles.RealMemberStates);
        var first = await State(cs);
        await Initialize(cs, SeedFiles.RealMemberStates);
        var second = await State(cs);

        Assert.Equal(first, second);
        Assert.Equal((150, 151, 4, 1, 304), (second.Countries, second.Periods, second.Users, second.Imports, second.AuditEvents));
    }

    [Fact]
    public async Task Changed_file_adds_only_missing_rows_and_never_overwrites_names()
    {
        var cs = await fixture.CreateEmptyDatabaseAsync();
        await Initialize(cs, SeedFiles.RealMemberStates);
        var before = await State(cs);

        var changed = File.ReadAllText(SeedFiles.RealMemberStates)
            .Replace("FRA,France,", "FRA,French Republic,")
            .TrimEnd() + "\r\nXYZ,Testland,2024-01-01,\r\n";
        await Initialize(cs, Write("member_states.csv", changed));

        await using var db = PostgresFixture.CreateContext(cs);
        Assert.Equal(151, await db.Countries.CountAsync());
        Assert.Equal(152, await db.CountryMemberships.CountAsync());
        Assert.Equal("France", (await db.Countries.SingleAsync(c => c.CountryCode == "FRA")).Name);
        Assert.Equal("Testland", (await db.Countries.SingleAsync(c => c.CountryCode == "XYZ")).Name);

        var second = await db.Imports.OrderBy(i => i.Id).LastAsync();
        Assert.Equal((1, 151), (second.NewCount, second.UnchangedCount));
        Assert.Contains("French Republic", second.Report);
        Assert.Equal(before.AuditEvents + 2, (await State(cs)).AuditEvents);
    }

    [Fact]
    public async Task Overlapping_periods_in_the_file_fail_clearly_and_write_nothing()
    {
        var cs = await fixture.CreateEmptyDatabaseAsync();
        var file = Write("overlap.csv", """
            country_code,country_name,member_from,member_to
            FRA,France,1995-01-01,
            ISL,Iceland,1995-01-01,2019-12-31
            ISL,Iceland,2019-06-01,
            """);

        var ex = await Assert.ThrowsAsync<DatabaseStartupException>(() => Initialize(cs, file));

        Assert.Contains("line 4: ISL 2019-06-01 to open overlaps line 3: ISL 1995-01-01 to 2019-12-31", ex.Message);
        Assert.Contains("overlap.csv", ex.Message);
        await AssertNoReferenceData(cs);
    }

    [Fact]
    public async Task Member_to_before_member_from_fails_clearly()
    {
        var cs = await fixture.CreateEmptyDatabaseAsync();
        var file = Write("reversed.csv", """
            country_code,country_name,member_from,member_to
            FRA,France,2020-01-01,2019-12-31
            """);

        var ex = await Assert.ThrowsAsync<DatabaseStartupException>(() => Initialize(cs, file));

        Assert.Contains("line 2: FRA member_to 2019-12-31 is before member_from 2020-01-01", ex.Message);
        await AssertNoReferenceData(cs);
    }

    [Fact]
    public async Task Bad_codes_and_dates_are_all_reported_with_line_numbers()
    {
        var cs = await fixture.CreateEmptyDatabaseAsync();
        var file = Write("bad.csv", """
            country_code,country_name,member_from,member_to
            fra,France,1995-01-01,
            DEU,Germany,01/01/1995,
            """);

        var ex = await Assert.ThrowsAsync<DatabaseStartupException>(() => Initialize(cs, file));

        Assert.Contains("line 2: country_code 'fra' is not three upper-case letters", ex.Message);
        Assert.Contains("line 3: member_from '01/01/1995' is not a yyyy-mm-dd date", ex.Message);
    }

    [Fact]
    public async Task Period_that_clashes_with_a_stored_period_fails_and_changes_nothing()
    {
        var cs = await fixture.CreateEmptyDatabaseAsync();
        await Initialize(cs, SeedFiles.RealMemberStates);
        var before = await State(cs);

        // NZL is stored as 1995-01-01 to 2021-06-30; this file says it never left.
        var reopened = File.ReadAllText(SeedFiles.RealMemberStates).Replace("NZL,New Zealand,1995-01-01,2021-06-30", "NZL,New Zealand,1995-01-01,");
        var ex = await Assert.ThrowsAsync<DatabaseStartupException>(() => Initialize(cs, Write("member_states.csv", reopened)));

        Assert.Contains("never overwritten", ex.Message);
        Assert.Contains("NZL 1995-01-01 to open overlaps stored period NZL 1995-01-01 to 2021-06-30", ex.Message);
        Assert.Equal(before, await State(cs));
    }

    [Fact]
    public async Task Empty_seed_setting_is_fine_when_countries_already_exist()
    {
        var cs = await fixture.CreateEmptyDatabaseAsync();
        await Initialize(cs, SeedFiles.RealMemberStates);
        var before = await State(cs);

        await Initialize(cs, memberStatesPath: null);

        Assert.Equal(before, await State(cs));
    }

    private async Task Initialize(string connectionString, string? memberStatesPath)
    {
        await using var db = PostgresFixture.CreateContext(connectionString);
        var initializer = new DatabaseInitializer(db, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync(migrate: true, memberStatesPath);
    }

    private string Write(string fileName, string content)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<List<(DateOnly, DateOnly?)>> Periods(MeridianDbContext db, string code) =>
        (await db.CountryMemberships.Where(m => m.CountryCode == code).OrderBy(m => m.MemberFrom).ToListAsync())
        .Select(m => (m.MemberFrom, m.MemberTo)).ToList();

    private static async Task AssertNoReferenceData(string connectionString)
    {
        await using var db = PostgresFixture.CreateContext(connectionString);
        Assert.Equal(0, await db.Countries.CountAsync());
        Assert.Equal(0, await db.CountryMemberships.CountAsync());
        Assert.Equal(0, await db.Imports.CountAsync());
    }

    /// <summary>Row counts plus a fingerprint of the reference data, so "changes nothing" also covers updated values.</summary>
    private static async Task<DbState> State(string connectionString)
    {
        await using var db = PostgresFixture.CreateContext(connectionString);
        var fingerprint = string.Join("|", await db.Countries.OrderBy(c => c.CountryCode).Select(c => c.CountryCode + c.Name).ToListAsync())
            + string.Join("|", (await db.CountryMemberships.OrderBy(m => m.Id).ToListAsync()).Select(m => $"{m.Id}{m.CountryCode}{m.MemberFrom}{m.MemberTo}"));
        return new DbState(
            await db.Countries.CountAsync(),
            await db.CountryMemberships.CountAsync(),
            await db.Users.CountAsync(),
            await db.Imports.CountAsync(),
            await db.AuditEvents.CountAsync(),
            await db.AuditEvents.MaxAsync(a => (long?)a.Id),
            fingerprint);
    }

    private sealed record DbState(int Countries, int Periods, int Users, int Imports, int AuditEvents, long? LastAuditId, string Fingerprint);
}
