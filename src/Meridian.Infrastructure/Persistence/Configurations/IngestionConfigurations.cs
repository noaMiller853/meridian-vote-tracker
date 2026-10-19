using Meridian.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Meridian.Infrastructure.Persistence.Configurations;

public class BackgroundJobConfiguration : IEntityTypeConfiguration<BackgroundJob>
{
    public void Configure(EntityTypeBuilder<BackgroundJob> b)
    {
        b.ToTable("background_jobs", t =>
        {
            t.HasCheckConstraint("ck_background_jobs_type", "type IN ('Import', 'Snapshot')");
            t.HasCheckConstraint("ck_background_jobs_status", "status IN ('Queued', 'Running', 'Succeeded', 'Failed')");
        });
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
    }
}

public class ImportConfiguration : IEntityTypeConfiguration<Import>
{
    public void Configure(EntityTypeBuilder<Import> b)
    {
        b.ToTable("imports", t =>
        {
            t.HasCheckConstraint("ck_imports_sha256", "sha256 ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("ck_imports_kind", "kind IN ('Votes', 'MemberStates')");
        });
        b.Property(x => x.Kind).HasDefaultValue(Import.KindVotes);
        b.Property(x => x.UploadedAt).HasDefaultValueSql("now()");
        b.Property(x => x.Report).HasColumnType("jsonb");
        // Not unique: re-uploading the same file is allowed and must be a no-op (ING-12), not an error.
        b.HasIndex(x => new { x.Kind, x.Sha256 });
        b.HasOne(x => x.UploadedBy).WithMany().HasForeignKey(x => x.UploadedById);
        b.HasOne(x => x.BackgroundJob).WithMany().HasForeignKey(x => x.BackgroundJobId);
    }
}

public class ImportRowConfiguration : IEntityTypeConfiguration<ImportRow>
{
    public void Configure(EntityTypeBuilder<ImportRow> b)
    {
        b.ToTable("import_rows", t => t.HasCheckConstraint("ck_import_rows_classification",
            "classification IN ('New', 'Unchanged', 'Duplicate', 'Invalid', 'Contradiction', 'Conflict')"));
        b.HasKey(x => new { x.ImportId, x.RowNumber });
        b.HasOne(x => x.Import).WithMany().HasForeignKey(x => x.ImportId);
    }
}
