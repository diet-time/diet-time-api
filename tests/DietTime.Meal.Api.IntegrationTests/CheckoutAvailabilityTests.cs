using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DietTime.Contracts;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Every_published_fixture_plan_package_and_duration_can_be_purchased_using_its_own_ids()
    {
        var setup = await ApprovalClient(); await using var factory = setup.Factory; using var client = setup.Client;
        var originalPlan = await db.MealPlanTemplates.SingleAsync();
        var secondPlan = new MealPlanTemplate { Id = Guid.NewGuid(), VersionGroupId = Guid.NewGuid(),
            Code = "SECOND_PLAN", IsActive = true, IsPublished = true, CycleWeeks = 4,
            Translations = [new MealPlanTemplateTranslation { Id = Guid.NewGuid(), LanguageCode = "en", Name = "Different display name" }] };
        for (var week = 1; week <= 4; week++)
            foreach (var day in Enum.GetValues<MenuWeekday>().Where(d => d != MenuWeekday.Friday))
                secondPlan.Days.Add(new MealPlanTemplateDay { Id = Guid.NewGuid(), WeekNumber = week, MenuWeekday = day, IsActive = true });
        db.Add(secondPlan);
        var small = new MealPackageOption { Id = Guid.NewGuid(), Name = "2 meals", MealCount = 2, SnackCount = 0, IsActive = true };
        db.Add(small);
        var originalPackage = await db.MealPackageOptions.SingleAsync();
        var six = new MealPlanPricePackage { Code = "SIX", NameEn = "6 service days", NameAr = "6 service days", DurationDays = 6, IsActive = true, IsDeliveryEnabled = false };
        var twentyFour = new MealPlanPricePackage { Code = "MONTH", NameEn = "24 service days", NameAr = "24 service days", DurationDays = 24, IsActive = true };
        db.AddRange(six, twentyFour);
        var durations = new[] { six, await db.MealPlanPricePackages.SingleAsync(), twentyFour };
        foreach (var plan in new[] { originalPlan, secondPlan })
        foreach (var package in new[] { originalPackage, small })
        foreach (var duration in durations)
        {
            if (plan == originalPlan && package == originalPackage && duration.DurationDays == 12) continue;
            db.Add(new MealPlanPrice { Id = Guid.NewGuid(), Plan = plan, PackageOption = package,
                PackageCode = duration.Code, DurationDays = duration.DurationDays, MealsPerDay = package.MealCount,
                SnacksPerDay = package.SnackCount, Amount = duration.DurationDays == 6 ? 699 : duration.DurationDays * 100,
                CurrencyCode = "QAR", IsActive = true, EffectiveFrom = clock.Now.AddDays(-1) });
        }
        var secondBranch = new TakeawayBranch { Id = Guid.NewGuid(), Name = "Diet Time – Izghawa Branch", IsActive = true };
        db.Add(secondBranch); await db.SaveChangesAsync();
        var purchases = 0;
        foreach (var plan in new[] { originalPlan, secondPlan })
        {
            var options = await client.GetFromJsonAsync<JsonElement>($"/api/v1/customer/meal-plans/{plan.Id}/purchase-options?language=en");
            Assert.Equal(plan.Id, options.GetProperty("data").GetProperty("plan").GetProperty("id").GetGuid());
            var configurations = options.GetProperty("data").GetProperty("mealConfigurations").EnumerateArray().ToArray();
            Assert.Equal(2, configurations.Length);
            foreach (var configuration in configurations)
            foreach (var price in configuration.GetProperty("packages").EnumerateArray())
            {
                var priceId = price.GetProperty("priceId").GetGuid();
                Assert.Equal(plan.Id, price.GetProperty("mealPlanId").GetGuid());
                Assert.Equal(configuration.GetProperty("packageOptionId").GetGuid(), price.GetProperty("packageOptionId").GetGuid());
                using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
                {
                    Content = JsonContent.Create(setup.Request with { MealPlanTemplateId = plan.Id, MealPlanPriceId = priceId,
                        TakeawayBranchId = purchases % 2 == 0 ? setup.Request.TakeawayBranchId : secondBranch.Id,
                        ExpectedTotalAmount = price.GetProperty("effectivePrice").GetDecimal() })
                };
                message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
                var response = await client.SendAsync(message);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                var saved = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("PENDING_APPROVAL", saved.GetProperty("status").GetString());
                Assert.Equal(priceId, saved.GetProperty("plan").GetProperty("mealPlanPriceId").GetGuid());
                Assert.Equal(price.GetProperty("effectivePrice").GetDecimal(), saved.GetProperty("pricing").GetProperty("totalAmount").GetDecimal());
                purchases++;
            }
        }
        Assert.Equal(12, purchases);
        Assert.Empty(await db.Set<SubscriptionServiceDay>().ToArrayAsync());
    }

    [UpcomingPostgresFact]
    public async Task Invalid_plan_package_and_price_relationships_return_distinct_order_error_codes()
    {
        var setup = await ApprovalClient(); await using var factory = setup.Factory; using var client = setup.Client;
        var price = await db.MealPlanPrices.Include(p => p.PackageOption).Include(p => p.Plan).SingleAsync();
        async Task Rejected(PlaceOrderRequest request, string code)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = JsonContent.Create(request) };
            message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var response = await client.SendAsync(message);
            Assert.False(response.IsSuccessStatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(code, json.GetProperty("code").GetString());
        }
        price.IsActive = false; await db.SaveChangesAsync(); await Rejected(setup.Request, "pricing_not_available");
        price.IsActive = true; price.PackageOption!.IsActive = false; await db.SaveChangesAsync(); await Rejected(setup.Request, "package_not_available");
        price.PackageOption.IsActive = true; price.MealsPerDay--; await db.SaveChangesAsync(); await Rejected(setup.Request, "invalid_plan_configuration");
        price.MealsPerDay++; price.Plan.IsActive = false; await db.SaveChangesAsync(); await Rejected(setup.Request, "plan_inactive");
        price.Plan.IsActive = true; await db.SaveChangesAsync();
        await Rejected(setup.Request with { MealPlanPriceId = Guid.NewGuid() }, "pricing_not_available");
        await Rejected(setup.Request with { MealPlanTemplateId = Guid.NewGuid() }, "plan_not_found");
        var other = new MealPlanTemplate { Id = Guid.NewGuid(), VersionGroupId = Guid.NewGuid(), Code = "OTHER", IsActive = true, IsPublished = true };
        db.Add(other); await db.SaveChangesAsync();
        await Rejected(setup.Request with { MealPlanTemplateId = other.Id }, "invalid_plan_configuration");
        Assert.Single(await db.Orders.ToArrayAsync());
    }
}
