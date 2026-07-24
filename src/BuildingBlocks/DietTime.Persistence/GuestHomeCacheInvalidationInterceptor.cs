using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DietTime.Persistence;

public sealed class GuestHomeCacheVersion
{
    private long version;

    public long Current => Interlocked.Read(ref version);
    public void Invalidate() => Interlocked.Increment(ref version);
}

public sealed class GuestHomeCacheInvalidationInterceptor(
    GuestHomeCacheVersion cacheVersion) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        InvalidateWhenContentChanged(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        InvalidateWhenContentChanged(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void InvalidateWhenContentChanged(DbContext? context)
    {
        if (context?.ChangeTracker.Entries().Any(entry =>
                (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted) &&
                (entry.Metadata.GetTableName() is
                    "meal_items" or
                    "meal_item_translations" or
                    "meal_nutrition" or
                    "meal_media" or
                    "meal_item_allergens" or
                    "allergens" or
                    "allergen_translations" or
                    "meal_types" or
                    "meal_type_translations" or
                    "meal_plan_templates" or
                    "meal_plan_template_translations" or
                    "meal_plan_template_days" or
                    "meal_plan_template_slots" or
                    "meal_plan_slot_options")) == true)
        {
            cacheVersion.Invalidate();
        }
    }
}
