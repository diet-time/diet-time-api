using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed class PurchasablePlanPricesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Thirteen_explicit_combinations_keep_their_configured_prices_and_ids()
    {
        var plan = Plan();
        var duration = Duration();
        var prices = Enumerable.Range(1, 13).Select(index => Price(plan, index)).ToArray();
        var result = PurchasablePlanPrices.Available(prices.AsQueryable(), new[] { duration }.AsQueryable(), new[] { plan }.AsQueryable(), Now).ToArray();
        Assert.Equal(13, result.Length);
        Assert.Equal(prices.Select(price => price.Id), result.Select(price => price.Id));
        Assert.Equal(prices.Select(price => price.Amount), result.Select(price => price.Amount));
    }

    [Theory]
    [InlineData("price-inactive")]
    [InlineData("future")]
    [InlineData("expired")]
    [InlineData("zero-price")]
    [InlineData("package-missing")]
    [InlineData("package-inactive")]
    [InlineData("counts-mismatch")]
    [InlineData("duration-code-missing")]
    [InlineData("duration-days-mismatch")]
    [InlineData("duration-inactive")]
    [InlineData("plan-inactive")]
    [InlineData("plan-draft")]
    [InlineData("plan-expired")]
    public void Unavailable_combinations_are_excluded(string reason)
    {
        var plan = Plan(); var duration = Duration(); var price = Price(plan);
        switch (reason)
        {
            case "price-inactive": price.IsActive = false; break;
            case "future": price.EffectiveFrom = Now.AddSeconds(1); break;
            case "expired": price.EffectiveUntil = Now; break;
            case "zero-price": price.Amount = 0; break;
            case "package-missing": price.PackageOption = null; price.PackageOptionId = null; break;
            case "package-inactive": price.PackageOption!.IsActive = false; break;
            case "counts-mismatch": price.PackageOption!.MealCount++; break;
            case "duration-code-missing": price.PackageCode = "WRONG"; break;
            case "duration-days-mismatch": price.DurationDays = 24; break;
            case "duration-inactive": duration.IsActive = false; break;
            case "plan-inactive": plan.IsActive = false; break;
            case "plan-draft": plan.IsPublished = false; break;
            case "plan-expired": plan.ValidUntil = new(2026, 10, 24); break;
        }
        Assert.Empty(PurchasablePlanPrices.Available(new[] { price }.AsQueryable(), new[] { duration }.AsQueryable(), new[] { plan }.AsQueryable(), Now));
    }

    [Fact]
    public void Newer_draft_does_not_hide_live_price_but_newer_published_version_does()
    {
        var live = Plan(); var newer = Plan(); newer.VersionGroupId = live.VersionGroupId; newer.VersionNumber = 2;
        live.IsLatest = false; newer.IsPublished = false;
        var query = () => PurchasablePlanPrices.Available(new[] { Price(live) }.AsQueryable(), new[] { Duration() }.AsQueryable(), new[] { live, newer }.AsQueryable(), Now);
        Assert.Single(query());
        newer.IsPublished = true;
        Assert.Empty(query());
    }

    [Fact]
    public void Same_day_count_cannot_substitute_a_different_duration_code()
    {
        var plan = Plan(); var duration = Duration(); duration.Code = "OTHER_WEEK";
        Assert.Empty(PurchasablePlanPrices.Available(new[] { Price(plan) }.AsQueryable(), new[] { duration }.AsQueryable(), new[] { plan }.AsQueryable(), Now));
    }

    [Fact]
    public void Availability_query_translates_to_postgresql_without_connecting()
    {
        using var db = new DietTimeDbContext(new DbContextOptionsBuilder<DietTimeDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=test;Password=test")
            .UseSnakeCaseNamingConvention().Options);
        var sql = PurchasablePlanPrices.Query(db, Now).Select(price => new { price.Id, price.Amount, price.PackageOption!.Name }).ToQueryString();
        Assert.Contains("package_option_id", sql);
        Assert.Contains("package_code", sql);
        Assert.Contains("meal_plan_price_packages", sql);
        Assert.Contains("effective_until", sql);
    }

    private static MealPlanTemplate Plan() => new() { Id = Guid.NewGuid(), VersionGroupId = Guid.NewGuid(), VersionNumber = 1, IsActive = true, IsPublished = true };
    private static MealPlanPricePackage Duration() => new() { Code = "WEEK", DurationDays = 6, IsActive = true };
    private static MealPlanPrice Price(MealPlanTemplate plan, int index = 1)
    {
        var option = new MealPackageOption { Id = Guid.NewGuid(), Name = $"Package {index}", MealCount = index, SnackCount = 1, IsActive = true };
        return new() { Id = Guid.NewGuid(), Plan = plan, MealPlanTemplateId = plan.Id,
            PackageCode = "WEEK", PackageOptionId = option.Id, PackageOption = option,
            DurationDays = 6, MealsPerDay = index, SnacksPerDay = 1,
            Amount = 500m + index, CurrencyCode = "QAR", IsActive = true, EffectiveFrom = Now.AddDays(-1) };
    }
}
