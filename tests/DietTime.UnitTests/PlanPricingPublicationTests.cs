using DietTime.Application;
using DietTime.Domain;

namespace DietTime.UnitTests;

public sealed class PlanPricingPublicationTests
{
    private readonly DateTimeOffset now = DateTimeOffset.Parse("2026-11-08T10:00:00Z");
    private readonly Guid actor = Guid.NewGuid();
    private static readonly MealPlanPricePackage[] Durations =
        [new() { Code = "WEEK", DurationDays = 6, IsActive = true }];

    private (MealPlanTemplate source, MealPlanTemplate target) Plans()
    {
        var group = Guid.NewGuid();
        var source = new MealPlanTemplate { Id = Guid.NewGuid(), VersionGroupId = group,
            VersionNumber = 1, DurationDays = 24, IsPublished = true };
        source.Prices.Add(new() { Id = Guid.NewGuid(), MealPlanTemplateId = source.Id,
            PackageCode = "WEEK", PackageOptionId = Guid.NewGuid(),
            PackageOption = new() { MealCount = 2, SnackCount = 1, IsActive = true },
            DurationDays = 6, MealsPerDay = 2, SnacksPerDay = 1, CurrencyCode = "QAR", Amount = 300,
            DiscountedPrice = 275, IsActive = true, EffectiveFrom = now.AddDays(-3), EffectiveUntil = now.AddDays(30),
            Translations = [new() { Id = Guid.NewGuid(), LanguageCode = "en", Name = "Weekly", Description = "Six service days" }] });
        var target = new MealPlanTemplate { Id = Guid.NewGuid(), VersionGroupId = group,
            VersionNumber = 2, DurationDays = 24, IsPublished = true };
        return (source, target);
    }

    [Fact]
    public void Publication_copies_terms_and_translations_without_reassigning_historical_prices()
    {
        var (source, target) = Plans();
        var original = source.Prices.Single();
        var copy = Assert.Single(PlanPricingPublication.Prepare(source, target, Durations, now, actor));
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal(target.Id, copy.MealPlanTemplateId);
        Assert.Equal(source.Id, original.MealPlanTemplateId);
        Assert.Equal(original.Amount, copy.Amount);
        Assert.Equal(original.DiscountedPrice, copy.DiscountedPrice);
        Assert.Equal(original.CurrencyCode, copy.CurrencyCode);
        Assert.Equal(original.EffectiveFrom, copy.EffectiveFrom);
        Assert.Equal(original.EffectiveUntil, copy.EffectiveUntil);
        Assert.Equal(original.PackageCode, copy.PackageCode);
        Assert.Equal(original.PackageOptionId, copy.PackageOptionId);
        Assert.Equal(original.DurationDays, copy.DurationDays);
        Assert.Equal(original.MealsPerDay, copy.MealsPerDay);
        Assert.Equal(original.SnacksPerDay, copy.SnacksPerDay);
        Assert.True(copy.IsActive);
        Assert.Equal(actor, copy.CreatedBy);
        Assert.Equal(now, copy.CreatedAt);
        var translation = Assert.Single(copy.Translations);
        Assert.NotSame(original.Translations.Single(), translation);
        Assert.NotEqual(original.Translations.Single().Id, translation.Id);
        Assert.Equal("Weekly", translation.Name);
        Assert.Equal("Six service days", translation.Description);
        Assert.Empty(target.Prices); // Preparation has no partial graph mutations on failure.
    }

    [Fact]
    public void Draft_saves_leave_live_pricing_unchanged()
    {
        var (source, target) = Plans();
        target.IsPublished = false;
        Assert.Empty(PlanPricingPublication.Prepare(source, target, Durations, now, actor));
        Assert.Single(source.Prices);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("expired")]
    public void Inactive_and_expired_prices_are_not_revived(string state)
    {
        var (source, target) = Plans();
        var price = source.Prices.Single();
        if (state == "inactive") price.IsActive = false;
        else price.EffectiveUntil = now;
        Assert.Empty(PlanPricingPublication.Prepare(source, target, Durations, now, actor));
    }

    [Fact]
    public void Scheduled_future_pricing_keeps_its_start_date()
    {
        var (source, target) = Plans();
        source.Prices.Single().EffectiveFrom = now.AddDays(7);
        var copy = Assert.Single(PlanPricingPublication.Prepare(source, target, Durations, now, actor));
        Assert.Equal(now.AddDays(7), copy.EffectiveFrom);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Deliberate_draft_pricing_wins_including_disabled_packages(bool active)
    {
        var (source, target) = Plans();
        var price = source.Prices.Single();
        target.Prices.Add(new() { PackageCode = price.PackageCode, PackageOptionId = price.PackageOptionId,
            Amount = 400, IsActive = active });
        Assert.Empty(PlanPricingPublication.Prepare(source, target, Durations, now, actor));
        Assert.Equal(400m, target.Prices.Single().Amount);
    }

    [Theory]
    [InlineData("meal count")]
    [InlineData("snack count")]
    [InlineData("inactive package")]
    [InlineData("missing package")]
    [InlineData("duration")]
    [InlineData("missing duration")]
    [InlineData("plan type")]
    [InlineData("plan duration")]
    [InlineData("customization")]
    public void Changed_commercial_terms_require_review_before_publication(string change)
    {
        var (source, target) = Plans();
        var price = source.Prices.Single();
        switch (change)
        {
            case "meal count": price.PackageOption!.MealCount++; break;
            case "snack count": price.PackageOption!.SnackCount++; break;
            case "inactive package": price.PackageOption!.IsActive = false; break;
            case "missing package": price.PackageOption = null; break;
            case "duration": price.DurationDays++; break;
            case "missing duration": price.PackageCode = null; break;
            case "plan type": target.PlanType = "CUSTOM"; break;
            case "plan duration": target.DurationDays++; break;
            case "customization": target.IsCustomizable = !source.IsCustomizable; break;
        }
        var error = Assert.Throws<TemplateDayException>(() =>
            PlanPricingPublication.Prepare(source, target, Durations, now, actor));
        Assert.Equal("PRICING_REVIEW_REQUIRED", error.Code);
        Assert.Equal(422, error.StatusCode);
        Assert.Empty(target.Prices);
    }

    [Fact]
    public void Inactive_duration_requires_review()
    {
        var (source, target) = Plans();
        Assert.Throws<TemplateDayException>(() => PlanPricingPublication.Prepare(source, target,
            [new() { Code = "WEEK", DurationDays = 6, IsActive = false }], now, actor));
    }

    [Fact]
    public void Repeated_preparation_does_not_duplicate_prices_already_carried_forward()
    {
        var (source, target) = Plans();
        foreach (var copy in PlanPricingPublication.Prepare(source, target, Durations, now, actor)) target.Prices.Add(copy);
        Assert.Empty(PlanPricingPublication.Prepare(source, target, Durations, now, actor));
        Assert.Single(target.Prices);
    }

    [Fact]
    public void Another_plan_cannot_supply_prices()
    {
        var (source, target) = Plans();
        target.VersionGroupId = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => PlanPricingPublication.Prepare(source, target, Durations, now, actor));
    }
}
