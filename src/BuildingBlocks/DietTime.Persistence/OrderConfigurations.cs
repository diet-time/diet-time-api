using DietTime.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DietTime.Persistence;

public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> e)
    {
        e.ToTable("orders", "public");
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        e.Property(x => x.OrderNumber).HasColumnName("order_number").HasMaxLength(30).IsRequired();
        e.Property(x => x.CustomerProfileId).HasColumnName("customer_profile_id");
        e.Property(x => x.MealPlanTemplateId).HasColumnName("meal_plan_template_id");
        e.Property(x => x.MealPlanPriceId).HasColumnName("meal_plan_price_id");
        e.Property(x => x.CustomerAddressId).HasColumnName("customer_address_id");
        e.Property(x => x.DeliveryTimeSlotId).HasColumnName("delivery_time_slot_id");
        e.Property(x => x.StartDate).HasColumnName("start_date");
        e.Property(x => x.EndDate).HasColumnName("end_date");
        e.Property(x => x.DeliveryDaysPerWeek).HasColumnName("delivery_days_per_week");
        e.Property(x => x.PlanName).HasColumnName("plan_name").HasMaxLength(200).IsRequired();
        e.Property(x => x.PlanDurationName).HasColumnName("plan_duration_name").HasMaxLength(150).IsRequired();
        Money(e.Property(x => x.Subtotal).HasColumnName("subtotal"));
        Money(e.Property(x => x.DiscountAmount).HasColumnName("discount_amount"));
        Money(e.Property(x => x.DeliveryCharge).HasColumnName("delivery_charge"));
        Money(e.Property(x => x.TotalAmount).HasColumnName("total_amount"));
        e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasColumnType("char(3)").IsRequired();
        e.Property(x => x.CouponCode).HasColumnName("coupon_code").HasMaxLength(100);
        e.Property(x => x.DeliveryAddressName).HasColumnName("delivery_address_name").HasMaxLength(100);
        e.Property(x => x.DeliveryAddressType).HasColumnName("delivery_address_type").HasMaxLength(30).IsRequired();
        e.Property(x => x.DeliveryBuildingNo).HasColumnName("delivery_building_no").HasMaxLength(50);
        e.Property(x => x.DeliveryStreetNo).HasColumnName("delivery_street_no").HasMaxLength(50);
        e.Property(x => x.DeliveryUnitNumber).HasColumnName("delivery_unit_number").HasMaxLength(50);
        e.Property(x => x.DeliveryZoneNo).HasColumnName("delivery_zone_no").HasMaxLength(50);
        e.Property(x => x.DeliveryArea).HasColumnName("delivery_area").HasMaxLength(150).IsRequired();
        e.Property(x => x.DeliveryDirections).HasColumnName("delivery_directions").HasMaxLength(500);
        e.Property(x => x.DeliveryLatitude).HasColumnName("delivery_latitude").HasPrecision(10, 7);
        e.Property(x => x.DeliveryLongitude).HasColumnName("delivery_longitude").HasPrecision(10, 7);
        e.Property(x => x.DeliveryFormattedAddress).HasColumnName("delivery_formatted_address").HasMaxLength(500);
        e.Property(x => x.DeliveryTimeSlotName).HasColumnName("delivery_time_slot_name").HasMaxLength(100).IsRequired();
        e.Property(x => x.DeliveryStartTime).HasColumnName("delivery_start_time").HasColumnType("time without time zone");
        e.Property(x => x.DeliveryEndTime).HasColumnName("delivery_end_time").HasColumnType("time without time zone");
        e.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        e.Property(x => x.PaymentStatus).HasColumnName("payment_status").HasMaxLength(30).IsRequired();
        e.Property(x => x.PlacedAt).HasColumnName("placed_at");
        e.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(100).IsRequired();
        e.Property(x => x.CreatedAt).HasColumnName("created_at");
        e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        e.Property(x => x.CreatedBy).HasColumnName("created_by");
        e.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        e.Property(x => x.RowVersion).HasColumnName("row_version").HasDefaultValue(1L).IsConcurrencyToken();
        e.HasIndex(x => x.OrderNumber).IsUnique().HasDatabaseName("ux_orders_order_number");
        e.HasIndex(x => x.IdempotencyKey).IsUnique().HasDatabaseName("ux_orders_idempotency_key");
        e.HasIndex(x => new { x.CustomerProfileId, x.PlacedAt }).HasDatabaseName("ix_orders_customer_placed_at");
        e.HasIndex(x => new { x.Status, x.StartDate, x.EndDate }).HasDatabaseName("ix_orders_status_service_dates");
    }

    private static void Money(PropertyBuilder<decimal> property) => property.HasPrecision(12, 2);
}

public sealed class OrderMealConfiguration : IEntityTypeConfiguration<OrderMeal>
{
    public void Configure(EntityTypeBuilder<OrderMeal> e)
    {
        e.ToTable("order_meals", "public");
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        e.Property(x => x.OrderId).HasColumnName("order_id");
        e.Property(x => x.MealTypeId).HasColumnName("meal_type_id");
        e.Property(x => x.MealTypeName).HasColumnName("meal_type_name").HasMaxLength(100).IsRequired();
        e.Property(x => x.Quantity).HasColumnName("quantity");
        e.HasIndex(x => new { x.OrderId, x.MealTypeId }).IsUnique().HasDatabaseName("ux_order_meals_order_type");
        e.HasOne(x => x.Order).WithMany(x => x.Meals).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OrderDeliveryDayConfiguration : IEntityTypeConfiguration<OrderDeliveryDay>
{
    public void Configure(EntityTypeBuilder<OrderDeliveryDay> e)
    {
        e.ToTable("order_delivery_days", "public");
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        e.Property(x => x.OrderId).HasColumnName("order_id");
        e.Property(x => x.DayOfWeek).HasColumnName("day_of_week");
        e.HasIndex(x => new { x.OrderId, x.DayOfWeek }).IsUnique().HasDatabaseName("ux_order_delivery_days_order_day");
        e.HasIndex(x => new { x.DayOfWeek, x.OrderId }).HasDatabaseName("ix_order_delivery_days_day_order");
        e.HasOne(x => x.Order).WithMany(x => x.DeliveryDays).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OrderStatusHistoryConfiguration : IEntityTypeConfiguration<OrderStatusHistory>
{
    public void Configure(EntityTypeBuilder<OrderStatusHistory> e)
    {
        e.ToTable("order_status_history", "public");
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        e.Property(x => x.OrderId).HasColumnName("order_id");
        e.Property(x => x.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
        e.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(500);
        e.Property(x => x.ChangedAt).HasColumnName("changed_at");
        e.HasIndex(x => new { x.OrderId, x.ChangedAt }).HasDatabaseName("ix_order_status_history_order_changed");
        e.HasOne(x => x.Order).WithMany(x => x.StatusHistory).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
    }
}
