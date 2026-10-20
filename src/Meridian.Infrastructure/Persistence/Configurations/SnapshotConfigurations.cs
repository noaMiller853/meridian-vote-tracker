using Meridian.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Meridian.Infrastructure.Persistence.Configurations;

public class SnapshotConfiguration : IEntityTypeConfiguration<Snapshot>
{
    public void Configure(EntityTypeBuilder<Snapshot> b)
    {
        b.ToTable("snapshots", t =>
        {
            t.HasCheckConstraint("ck_snapshots_status", "status IN ('Calculating', 'Saved', 'Failed', 'Published')");
            t.HasCheckConstraint("ck_snapshots_quarter_format", "quarter IS NULL OR quarter ~ '^[0-9]{4}-Q[1-4]$'");
            t.HasCheckConstraint("ck_snapshots_published",
                "status <> 'Published' OR (quarter IS NOT NULL AND published_by_id IS NOT NULL AND published_at IS NOT NULL)");
        });
        b.Property(x => x.Filter).HasColumnType("jsonb");
        b.Property(x => x.Rules).HasColumnType("jsonb");
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

        // SNP-05: one published snapshot per quarter.
        b.HasIndex(x => x.Quarter)
            .IsUnique()
            .HasFilter("status = 'Published'")
            .HasDatabaseName("ux_snapshots_published_quarter");

        b.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById);
        b.HasOne(x => x.PublishedBy).WithMany().HasForeignKey(x => x.PublishedById);
        b.HasOne(x => x.BackgroundJob).WithMany().HasForeignKey(x => x.BackgroundJobId);
        b.HasOne(x => x.ReplacedSnapshot).WithMany().HasForeignKey(x => x.ReplacedSnapshotId);
    }
}

public class SnapshotVoteConfiguration : IEntityTypeConfiguration<SnapshotVote>
{
    public void Configure(EntityTypeBuilder<SnapshotVote> b)
    {
        b.ToTable("snapshot_votes", t =>
        {
            t.HasCheckConstraint("ck_snapshot_votes_desired_outcome", "desired_outcome IS NULL OR desired_outcome IN ('Y', 'N')");
            t.HasCheckConstraint("ck_snapshot_votes_weight", "weight IS NULL OR weight BETWEEN 1 AND 5");
            t.HasCheckConstraint("ck_snapshot_votes_included",
                "NOT included OR (desired_outcome IS NOT NULL AND weight IS NOT NULL)");
            // An included vote has a place in the frozen strings; an excluded one has none and says why.
            t.HasCheckConstraint("ck_snapshot_votes_ordinal",
                "(included AND ordinal >= 0 AND reason IS NULL) OR (NOT included AND ordinal IS NULL AND reason IS NOT NULL)");
        });
        b.HasKey(x => new { x.SnapshotId, x.VoteId });
        b.HasIndex(x => new { x.SnapshotId, x.Ordinal }).IsUnique().HasDatabaseName("ux_snapshot_votes_ordinal");
        b.HasOne(x => x.Snapshot).WithMany(s => s.Votes).HasForeignKey(x => x.SnapshotId);
        b.HasOne(x => x.Vote).WithMany().HasForeignKey(x => x.VoteId);
    }
}

public class SnapshotScoreConfiguration : IEntityTypeConfiguration<SnapshotScore>
{
    public void Configure(EntityTypeBuilder<SnapshotScore> b)
    {
        b.ToTable("snapshot_scores", t =>
        {
            t.HasCheckConstraint("ck_snapshot_scores_rank", "(score IS NULL) = (rank IS NULL)");
            t.HasCheckConstraint("ck_snapshot_scores_strings", "length(ballots) = length(outcomes)");
            t.HasCheckConstraint("ck_snapshot_scores_score", "score IS NULL OR score BETWEEN 0 AND 100");
        });
        b.HasKey(x => new { x.SnapshotId, x.CountryCode });
        b.Property(x => x.Score).HasPrecision(9, 6);
        b.HasOne(x => x.Snapshot).WithMany(s => s.Scores).HasForeignKey(x => x.SnapshotId);
        b.HasOne(x => x.Country).WithMany().HasForeignKey(x => x.CountryCode);
    }
}
