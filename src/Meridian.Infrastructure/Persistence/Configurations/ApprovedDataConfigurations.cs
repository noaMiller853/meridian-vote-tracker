using Meridian.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Meridian.Infrastructure.Persistence.Configurations;

public class VoteConfiguration : IEntityTypeConfiguration<Vote>
{
    public void Configure(EntityTypeBuilder<Vote> b)
    {
        b.ToTable("votes");
        b.HasIndex(x => x.AssemblyVoteId).IsUnique().HasDatabaseName("ux_votes_assembly_vote_id");
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.HasOne(x => x.FirstImport).WithMany().HasForeignKey(x => x.FirstImportId);
    }
}

public class CountryVoteConfiguration : IEntityTypeConfiguration<CountryVote>
{
    public void Configure(EntityTypeBuilder<CountryVote> b)
    {
        b.ToTable("country_votes", t => t.HasCheckConstraint("ck_country_votes_ballot", "ballot IN ('Y', 'N', 'A', 'X')"));
        b.HasKey(x => new { x.VoteId, x.CountryCode });
        b.HasIndex(x => x.CountryCode);
        b.HasOne(x => x.Vote).WithMany(v => v.CountryVotes).HasForeignKey(x => x.VoteId);
        b.HasOne(x => x.Country).WithMany().HasForeignKey(x => x.CountryCode);
        b.HasOne(x => x.SourceRow).WithMany().HasForeignKey(x => new { x.SourceImportId, x.SourceRowNumber });
        b.HasOne(x => x.ApprovedBy).WithMany().HasForeignKey(x => x.ApprovedById);
    }
}

public class VotePositionConfiguration : IEntityTypeConfiguration<VotePosition>
{
    public void Configure(EntityTypeBuilder<VotePosition> b)
    {
        b.ToTable("vote_positions", t =>
        {
            t.HasCheckConstraint("ck_vote_positions_desired_outcome", "desired_outcome IN ('Y', 'N')");
            t.HasCheckConstraint("ck_vote_positions_weight", "weight BETWEEN 1 AND 5");
        });
        b.HasKey(x => x.VoteId);
        b.HasOne(x => x.Vote).WithOne(v => v.Position).HasForeignKey<VotePosition>(x => x.VoteId);
        b.HasOne(x => x.SetBy).WithMany().HasForeignKey(x => x.SetById);
    }
}
