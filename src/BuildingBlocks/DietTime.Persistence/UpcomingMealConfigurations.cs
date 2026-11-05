using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public static class UpcomingMealConfigurations
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<PackageChangeRequest>(e => {
            e.ToTable("package_change_requests", "public");
            e.HasOne(r => r.Order).WithMany().HasForeignKey(r => r.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.Property(r => r.PayloadJson).HasColumnType("jsonb");
            e.Property(r => r.Type).HasMaxLength(20);
            e.Property(r => r.Status).HasMaxLength(30);
            e.Property(r => r.Reason).HasMaxLength(500);
            e.Property(r => r.ReviewReason).HasMaxLength(500);
            e.HasIndex(r => r.OrderId).IsUnique().HasFilter("status = 'PENDING_APPROVAL'")
                .HasDatabaseName("ux_package_change_requests_pending_order");
            e.HasIndex(r => new { r.OrderId, r.CreatedAt });
        });
        b.Entity<SubscriptionPause>(e => {
            e.ToTable("subscription_pauses", "public");
            e.HasOne(p => p.Order).WithMany().HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.Property(p => p.ScheduleChangesJson).HasColumnType("jsonb");
            e.HasIndex(p => new { p.OrderId, p.CreatedAt });
            e.HasIndex(p => p.ResumeCommandId).IsUnique().HasFilter("resume_command_id IS NOT NULL");
        });
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
