using Meridian.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Infrastructure.Persistence;

public class MeridianDbContext(DbContextOptions<MeridianDbContext> options) : DbContext(options)
{
    public DbSet<Country> Countries => Set<Country>();
    public DbSet<CountryMembership> CountryMemberships => Set<CountryMembership>();
    public DbSet<User> Users => Set<User>();
    public DbSet<BackgroundJob> BackgroundJobs => Set<BackgroundJob>();
    public DbSet<Import> Imports => Set<Import>();
    public DbSet<ImportRow> ImportRows => Set<ImportRow>();
    public DbSet<ImportVote> ImportVotes => Set<ImportVote>();
    public DbSet<Vote> Votes => Set<Vote>();
    public DbSet<CountryVote> CountryVotes => Set<CountryVote>();
    public DbSet<VotePosition> VotePositions => Set<VotePosition>();
    public DbSet<Conflict> Conflicts => Set<Conflict>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Snapshot> Snapshots => Set<Snapshot>();
    public DbSet<SnapshotVote> SnapshotVotes => Set<SnapshotVote>();
    public DbSet<SnapshotScore> SnapshotScores => Set<SnapshotScore>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Needed for the no-overlap exclusion constraint on country_memberships.
        modelBuilder.HasPostgresExtension("btree_gist");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MeridianDbContext).Assembly);

        // Approved data, imports and snapshots are never deleted, so nothing cascades.
        foreach (var fk in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
            fk.DeleteBehavior = DeleteBehavior.Restrict;
    }
}
