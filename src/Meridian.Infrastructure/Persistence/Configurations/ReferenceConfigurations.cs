using Meridian.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Meridian.Infrastructure.Persistence.Configurations;

public class CountryConfiguration : IEntityTypeConfiguration<Country>
{
    public void Configure(EntityTypeBuilder<Country> b)
    {
        b.ToTable("countries", t => t.HasCheckConstraint("ck_countries_country_code", "country_code ~ '^[A-Z]{3}$'"));
        b.HasKey(x => x.CountryCode);
    }
}

public class CountryMembershipConfiguration : IEntityTypeConfiguration<CountryMembership>
{
    public void Configure(EntityTypeBuilder<CountryMembership> b)
    {
        // The no-overlap exclusion constraint (btree_gist) cannot be modelled in EF; it is added as SQL in the migration.
        b.ToTable("country_memberships", t => t.HasCheckConstraint(
            "ck_country_memberships_period", "member_to IS NULL OR member_to >= member_from"));
        b.HasOne(x => x.Country).WithMany(c => c.Memberships).HasForeignKey(x => x.CountryCode);
    }
}

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users", t => t.HasCheckConstraint("ck_users_role", "role IN ('Analyst', 'Admin')"));
        b.HasIndex(x => x.Name).IsUnique();
    }
}
