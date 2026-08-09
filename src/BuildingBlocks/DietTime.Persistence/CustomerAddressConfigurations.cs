using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DietTime.Persistence;

public sealed class CustomerAddressConfiguration : IEntityTypeConfiguration<CustomerAddress>
{
    public void Configure(EntityTypeBuilder<CustomerAddress> entity)
    {
        entity.ToTable("customer_addresses", "public", table =>
        {
            table.HasCheckConstraint("ck_customer_addresses_type", "address_type IN ('HOME', 'APARTMENT', 'OFFICE', 'OTHER')");
            table.HasCheckConstraint("ck_customer_addresses_latitude", "latitude IS NULL OR latitude BETWEEN -90 AND 90");
            table.HasCheckConstraint("ck_customer_addresses_longitude", "longitude IS NULL OR longitude BETWEEN -180 AND 180");
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        entity.Property(x => x.CustomerProfileId).HasColumnName("customer_profile_id").IsRequired();
        entity.Property(x => x.AddressName).HasColumnName("address_name").HasMaxLength(100);
        entity.Property(x => x.AddressType).HasColumnName("address_type").HasMaxLength(30).IsRequired();
        entity.Property(x => x.BuildingNo).HasColumnName("building_no").HasMaxLength(50);
        entity.Property(x => x.StreetNo).HasColumnName("street_no").HasMaxLength(50);
        entity.Property(x => x.UnitNumber).HasColumnName("unit_number").HasMaxLength(50);
        entity.Property(x => x.ZoneNo).HasColumnName("zone_no").HasMaxLength(50);
        entity.Property(x => x.Area).HasColumnName("area").HasMaxLength(150).IsRequired();
        entity.Property(x => x.Directions).HasColumnName("directions").HasMaxLength(500);
        entity.Property(x => x.Latitude).HasColumnName("latitude").HasPrecision(10, 7);
        entity.Property(x => x.Longitude).HasColumnName("longitude").HasPrecision(10, 7);
        entity.Property(x => x.FormattedAddress).HasColumnName("formatted_address").HasMaxLength(500);
        entity.Property(x => x.IsDefault).HasColumnName("is_default").HasDefaultValue(false).IsRequired();
        entity.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true).IsRequired();
        entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        entity.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();
        entity.Property(x => x.CreatedBy).HasColumnName("created_by");
        entity.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        entity.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1L).IsConcurrencyToken();
        entity.HasIndex(x => x.CustomerProfileId).HasDatabaseName("ix_customer_addresses_customer_profile_id");
        entity.HasIndex(x => new { x.CustomerProfileId, x.IsActive }).HasDatabaseName("ix_customer_addresses_profile_active");
        entity.HasIndex(x => x.CustomerProfileId)
            .IsUnique()
            .HasFilter("is_active = true AND is_default = true")
            .HasDatabaseName("ux_customer_addresses_active_default");
        entity.HasOne(x => x.CustomerProfile)
            .WithMany(x => x.Addresses)
            .HasForeignKey(x => x.CustomerProfileId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class DeliveryTimeSlotConfiguration : IEntityTypeConfiguration<DeliveryTimeSlot>
{
    public void Configure(EntityTypeBuilder<DeliveryTimeSlot> entity)
    {
        entity.ToTable("delivery_time_slots", "public", table =>
            table.HasCheckConstraint("ck_delivery_time_slots_time_range", "end_time > start_time"));
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        entity.Property(x => x.Code).HasColumnName("code").HasMaxLength(50).IsRequired();
        entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        entity.Property(x => x.NameAr).HasColumnName("name_ar").HasMaxLength(100).IsRequired();
        entity.Property(x => x.StartTime).HasColumnName("start_time").HasColumnType("time without time zone").IsRequired();
        entity.Property(x => x.EndTime).HasColumnName("end_time").HasColumnType("time without time zone").IsRequired();
        entity.Property(x => x.SortOrder).HasColumnName("sort_order").IsRequired();
        entity.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true).IsRequired();
        entity.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()").IsRequired();
        entity.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()").IsRequired();
        entity.Property(x => x.CreatedBy).HasColumnName("created_by");
        entity.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        entity.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1L).IsConcurrencyToken();
        entity.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_delivery_time_slots_code");
        entity.HasIndex(x => new { x.IsActive, x.SortOrder }).HasDatabaseName("ix_delivery_time_slots_active_sort");
    }
}
