using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class MealConfigurationModelTests
{
    private static DietTimeDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DietTimeDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=test;Password=test")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new(options);
    }

    [Theory]
    [InlineData(typeof(MealPackageOption), "meal_package_options")]
    [InlineData(typeof(MealPackageOptionType), "meal_package_option_types")]
    [InlineData(typeof(MealPlanWeekday), "meal_plan_weekdays")]
    [InlineData(typeof(MealPlanDayItem), "meal_plan_day_items")]
    public void Configuration_entities_map_to_existing_tables(Type entityType, string table)
    {
        using var db = CreateContext();
        Assert.Equal(table, db.Model.FindEntityType(entityType)!.GetTableName());
    }

    [Fact]
    public void Day_items_reference_the_existing_meal_item_master()
    {
        using var db = CreateContext();
        var entity = db.Model.FindEntityType(typeof(MealPlanDayItem))!;
        var foreignKey = entity.GetForeignKeys().Single(x => x.Properties.Single().Name == nameof(MealPlanDayItem.MenuItemId));
        Assert.Equal(typeof(MealItem), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal("meal_items", foreignKey.PrincipalEntityType.GetTableName());
    }

    [Fact]
    public void Pricing_uses_existing_package_code_for_duration_without_removing_legacy_fields()
    {
        using var db = CreateContext();
        var entity = db.Model.FindEntityType(typeof(MealPlanPrice))!;
        Assert.NotNull(entity.FindProperty(nameof(MealPlanPrice.PackageOptionId)));
        Assert.Equal("package_code", entity.FindProperty(nameof(MealPlanPrice.PackageCode))!.GetColumnName());
        Assert.NotNull(entity.FindProperty(nameof(MealPlanPrice.DurationDays)));
        Assert.NotNull(entity.FindProperty(nameof(MealPlanPrice.MealPlanTemplateId)));
        Assert.DoesNotContain(db.Model.GetEntityTypes(), x => x.GetTableName() == "durations");
        Assert.DoesNotContain(entity.GetProperties(), x => x.GetColumnName() == "duration_id");
    }
}
