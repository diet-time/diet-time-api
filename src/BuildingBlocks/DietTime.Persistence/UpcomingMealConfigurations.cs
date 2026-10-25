using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public static class UpcomingMealConfigurations
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<SubscriptionServiceDay>(e =>
        {
            e.ToTable("subscription_service_days", "public");
            e.HasIndex(x => new { x.SubscriptionId, x.ServiceDate }).IsUnique().HasDatabaseName("uq_subscription_service_days");
            e.HasIndex(x => new { x.OrderId, x.ServiceDate }).IsUnique().HasFilter("order_id IS NOT NULL")
                .HasDatabaseName("ux_subscription_service_days_order_date");
            e.Property(x => x.Revision).IsConcurrencyToken();
            e.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<CustomerMealSelection>(e =>
        {
            e.ToTable("customer_meal_selections", "public");
            e.HasOne(x => x.ServiceDay).WithMany(x => x.Selections)
                .HasForeignKey(x => x.SubscriptionServiceDayId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.SubscriptionServiceDayId, x.Category, x.SelectionNumber })
                .IsUnique().HasFilter("category IS NOT NULL").HasDatabaseName("ux_customer_meal_selection_position");
            e.HasIndex(x => new { x.SubscriptionServiceDayId, x.MenuItemId })
                .IsUnique().HasFilter("category IS NOT NULL AND status <> 'CANCELLED'")
                .HasDatabaseName("ux_customer_meal_selection_dish");
        });
        b.Entity<CustomerMealSelectionHistory>(e => e.ToTable("customer_meal_selection_history", "public"));
    }
}
