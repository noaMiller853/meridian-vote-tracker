using System.Text.Json;
using Meridian.Domain.Membership;
using Meridian.Infrastructure.Persistence;
using Meridian.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Meridian.Infrastructure.Startup;

/// <summary>
/// Brings the database into a usable state at startup: migrations, users, member states.
/// Idempotent, never deletes or overwrites, and throws <see cref="DatabaseStartupException"/> when the API must not start.
/// </summary>
public sealed class DatabaseInitializer(MeridianDbContext db, ILogger<DatabaseInitializer> logger)
{
    public const string SystemUserName = "system";

    // Serializes concurrent starts (several API instances) while seeding. Arbitrary but fixed.
    private const long SeedLockKey = 7_302_114_001;

    private static readonly (string Name, string Role)[] DemoUsers =
    [
        ("Dana", "Analyst"),
        ("Omar", "Analyst"),
        ("Lena", "Admin"),
    ];

    /// <param name="memberStatesPath">Absolute path to member_states.csv, or null/empty to skip loading.</param>
    public async Task InitializeAsync(bool migrate, string? memberStatesPath, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(migrate, ct);

        // Validate the file before opening the transaction, so a bad file never leaves a lock or partial write.
        var file = string.IsNullOrWhiteSpace(memberStatesPath) ? null : MemberStatesFile.Read(memberStatesPath);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({SeedLockKey})", ct);

        var system = await db.Users.SingleOrDefaultAsync(u => u.Name == SystemUserName, ct)
            ?? throw new DatabaseStartupException($"The '{SystemUserName}' user is missing; it is created by the first migration.");

        await SeedUsersAsync(system, ct);

        if (file is null)
            logger.LogInformation("Seed:MemberStatesPath is empty; member states are not loaded.");
        else
            await SeedMemberStatesAsync(file, system, ct);

        if (!await db.Countries.AnyAsync(ct))
            throw new DatabaseStartupException(
                "The database has no countries, and every import is validated against them. " +
                "Set Seed:MemberStatesPath to seed/member_states.csv so they are loaded at startup.");

        await tx.CommitAsync(ct);
    }

    private async Task EnsureSchemaAsync(bool migrate, CancellationToken ct)
    {
        if (migrate)
        {
            logger.LogInformation("Applying database migrations (Database:MigrateOnStartup=true).");
            await db.Database.MigrateAsync(ct);
            return;
        }

        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
            throw new DatabaseStartupException(
                $"The database has {pending.Count} pending migration(s) ({string.Join(", ", pending)}). " +
                "Run 'dotnet ef database update' or set Database:MigrateOnStartup=true.");
    }

    private async Task SeedUsersAsync(User system, CancellationToken ct)
    {
        var existing = await db.Users.Select(u => u.Name).ToListAsync(ct);
        var added = DemoUsers
            .Where(u => !existing.Contains(u.Name))
            .Select(u => new User { Name = u.Name, Role = u.Role })
            .ToList();
        if (added.Count == 0)
            return;

        db.Users.AddRange(added);
        await db.SaveChangesAsync(ct);

        db.AuditEvents.AddRange(added.Select(u =>
            Created("user", u.Id.ToString(), new { u.Name, u.Role }, system, importId: null)));
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded users: {Users}.", string.Join(", ", added.Select(u => u.Name)));
    }

    private async Task SeedMemberStatesAsync(MemberStatesFile file, User system, CancellationToken ct)
    {
        if (await db.Imports.AnyAsync(i => i.Kind == Import.KindMemberStates && i.Sha256 == file.Sha256, ct))
        {
            logger.LogInformation("{File} (sha256 {Sha256}) is already loaded; nothing to do.", file.FileName, file.Sha256);
            return;
        }

        var countries = await db.Countries.ToDictionaryAsync(c => c.CountryCode, ct);
        var stored = (await db.CountryMemberships.ToListAsync(ct))
            .Select(m => new MembershipPeriod(m.CountryCode, m.MemberFrom, m.MemberTo))
            .ToList();

        var newPeriods = file.Rows.Where(r => !stored.Contains(r.Period)).ToList();

        // Reference data is never overwritten: a new period that overlaps a stored one stops the load.
        var clashes = newPeriods
            .SelectMany(r => stored.Where(s => s.Overlaps(r.Period)).Select(s => $"line {r.Line}: {r.Period} overlaps stored period {s}"))
            .ToList();
        if (clashes.Count > 0)
            throw new DatabaseStartupException(
                $"{file.FileName} conflicts with membership periods already in the database. " +
                "Reference data is never overwritten automatically; fix the file or the database by hand:" +
                Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", clashes));

        var newCountries = file.Rows
            .Where(r => !countries.ContainsKey(r.Period.CountryCode))
            .DistinctBy(r => r.Period.CountryCode)
            .Select(r => new Country { CountryCode = r.Period.CountryCode, Name = r.CountryName })
            .ToList();
        var namesIgnored = file.Rows
            .Where(r => countries.TryGetValue(r.Period.CountryCode, out var c) && c.Name != r.CountryName)
            .DistinctBy(r => r.Period.CountryCode)
            .Select(r => new { r.Period.CountryCode, StoredName = countries[r.Period.CountryCode].Name, FileName = r.CountryName })
            .ToList();

        var import = new Import
        {
            Kind = Import.KindMemberStates,
            FileName = file.FileName,
            StorageUri = $"seed://{file.FileName}",
            Sha256 = file.Sha256,
            UploadedById = system.Id,
            UploadedAt = DateTimeOffset.UtcNow,
            NewCount = newPeriods.Count,
            UnchangedCount = file.Rows.Count - newPeriods.Count,
            Report = JsonSerializer.Serialize(new
            {
                rows = file.Rows.Count,
                countriesAdded = newCountries.Count,
                periodsAdded = newPeriods.Count,
                namesIgnored,
            }),
        };
        var memberships = newPeriods
            .Select(r => new CountryMembership { CountryCode = r.Period.CountryCode, MemberFrom = r.Period.From, MemberTo = r.Period.To })
            .ToList();

        db.Imports.Add(import);
        db.Countries.AddRange(newCountries);
        db.CountryMemberships.AddRange(memberships);
        await db.SaveChangesAsync(ct);

        db.AuditEvents.AddRange(newCountries.Select(c =>
            Created("country", c.CountryCode, new { c.CountryCode, c.Name }, system, import.Id)));
        db.AuditEvents.AddRange(memberships.Select(m =>
            Created("country_membership", m.Id.ToString(), new { m.CountryCode, m.MemberFrom, m.MemberTo }, system, import.Id)));
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Loaded {File} as import {ImportId}: {Countries} countries and {Periods} periods added, {Unchanged} rows already present, {Names} names ignored.",
            file.FileName, import.Id, newCountries.Count, newPeriods.Count, import.UnchangedCount, namesIgnored.Count);
    }

    private static AuditEvent Created(string entity, string entityId, object newValue, User actor, long? importId) => new()
    {
        Entity = entity,
        EntityId = entityId,
        Action = "Created",
        NewValue = JsonSerializer.Serialize(newValue),
        ActorId = actor.Id,
        ImportId = importId,
        OccurredAt = DateTimeOffset.UtcNow,
    };
}
