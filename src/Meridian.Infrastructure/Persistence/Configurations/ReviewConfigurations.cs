using Meridian.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Meridian.Infrastructure.Persistence.Configurations;

public class ConflictConfiguration : IEntityTypeConfiguration<Conflict>
{
    public void Configure(EntityTypeBuilder<Conflict> b)
    {
        b.ToTable("conflicts", t =>
        {
            t.HasCheckConstraint("ck_conflicts_kind", "kind IN ('CountryVote', 'VoteField')");
            t.HasCheckConstraint("ck_conflicts_target",
                "(kind = 'CountryVote' AND country_code IS NOT NULL AND field_name IS NULL) OR " +
                "(kind = 'VoteField' AND country_code IS NULL AND field_name IN ('session', 'vote_date', 'title'))");
            t.HasCheckConstraint("ck_conflicts_ballot_values",
                "kind <> 'CountryVote' OR (approved_value IN ('Y', 'N', 'A', 'X') AND proposed_value IN ('Y', 'N', 'A', 'X'))");
            t.HasCheckConstraint("ck_conflicts_status", "status IN ('Pending', 'Approved', 'Rejected')");
            t.HasCheckConstraint("ck_conflicts_decision",
                "(status = 'Pending') = (decided_by_id IS NULL AND decided_at IS NULL)");
        });

        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

        // Only one open conflict per target and proposed value, so a re-upload never duplicates a pending conflict (ING-12).
        // Partial on purpose: decided conflicts must not block a later change of the same value (Y -> A -> Y -> A).
        b.HasIndex(x => new { x.VoteId, x.Kind, x.CountryCode, x.FieldName, x.ProposedValue })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasFilter("status = 'Pending'")
            .HasDatabaseName("ux_conflicts_pending_target");
        b.HasIndex(x => x.Status);

        b.HasOne(x => x.Vote).WithMany(v => v.Conflicts).HasForeignKey(x => x.VoteId);
        b.HasOne(x => x.Country).WithMany().HasForeignKey(x => x.CountryCode);
        b.HasOne(x => x.ApprovedSourceImport).WithMany().HasForeignKey(x => x.ApprovedSourceImportId);
        b.HasOne(x => x.ProposedSourceRow).WithMany()
            .HasForeignKey(x => new { x.ProposedSourceImportId, x.ProposedSourceRowNumber });
        b.HasOne(x => x.DecidedBy).WithMany().HasForeignKey(x => x.DecidedById);
    }
}

public class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.ToTable("audit_events");
        b.Property(x => x.OldValue).HasColumnType("jsonb");
        b.Property(x => x.NewValue).HasColumnType("jsonb");
        b.Property(x => x.OccurredAt).HasDefaultValueSql("now()");
        b.HasIndex(x => new { x.VoteId, x.OccurredAt });
        b.HasOne(x => x.Vote).WithMany().HasForeignKey(x => x.VoteId);
        b.HasOne(x => x.Actor).WithMany().HasForeignKey(x => x.ActorId);
        b.HasOne(x => x.Import).WithMany().HasForeignKey(x => x.ImportId);
    }
}
