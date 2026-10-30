using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DietTime.Persistence;

public sealed class TakeawayBranchConfiguration : IEntityTypeConfiguration<TakeawayBranch>
{
    public void Configure(EntityTypeBuilder<TakeawayBranch> e)
    {
        e.ToTable("takeaway_branches", "public");
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        e.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_takeaway_branches_name");
        e.Property(x => x.Address).HasColumnName("address").HasMaxLength(500);
        e.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        e.Property(x => x.CreatedAt).HasColumnName("created_at");
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        e.Property(x => x.CreatedBy).HasColumnName("created_by");
        e.Property(x => x.UpdatedBy).HasColumnName("updated_by");
    }
}
