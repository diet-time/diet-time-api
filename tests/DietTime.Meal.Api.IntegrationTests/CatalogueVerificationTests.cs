using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DietTime.Contracts;
using DietTime.Domain;
using DietTime.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Meal.Api.IntegrationTests;

public sealed partial class UpcomingMealServiceTests
{
    [UpcomingPostgresFact]
    public async Task Meal_status_filter_applies_before_pagination_and_total_count()
    {
        var setup = await ApprovalClient(); await using var factory = setup.Factory; using var client = setup.Client;
        var meals = await db.MealItems.Where(m => m.IsLatest).OrderBy(m => m.Sku).ToArrayAsync();
        meals[0].Status = "INACTIVE"; meals[1].Status = "DRAFT"; meals[2].Status = "ARCHIVED";
        await db.SaveChangesAsync();
        foreach (var status in new[] { "ACTIVE", "INACTIVE", "DRAFT", "ARCHIVED" })
        {
            var response = await client.GetFromJsonAsync<JsonElement>($"/api/v1/admin/meals?status={status.ToLowerInvariant()}&pageSize=1");
            Assert.Equal(meals.Count(m => m.Status == status), response.GetProperty("meta").GetProperty("totalCount").GetInt32());
            var row = Assert.Single(response.GetProperty("data").EnumerateArray());
            Assert.Equal(status, row.GetProperty("status").GetString());
        }
        var filtered = await client.GetFromJsonAsync<JsonElement>($"/api/v1/admin/meals?status=INACTIVE&search={Uri.EscapeDataString(meals[0].Sku)}");
        Assert.Equal(1, filtered.GetProperty("meta").GetProperty("totalCount").GetInt32());
        var second = await client.GetFromJsonAsync<JsonElement>("/api/v1/admin/meals?status=INACTIVE&page=2&pageSize=1");
        Assert.Empty(second.GetProperty("data").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/admin/meals?status=unknown")).StatusCode);
        var all = await client.GetFromJsonAsync<JsonElement>("/api/v1/admin/meals");
        Assert.Equal(meals.Length, all.GetProperty("meta").GetProperty("totalCount").GetInt32());
    }

    [UpcomingPostgresFact]
    public async Task Customer_sorting_is_global_stable_and_counts_only_booked_plans()
    {
        var setup = await ApprovalClient(); await using var factory = setup.Factory; using var client = setup.Client;
        var zeta = new CustomerProfile { Id = Guid.NewGuid(), PreferredName = "Zeta", CreatedAt = clock.Now.AddDays(1) };
        var alpha = new CustomerProfile { Id = Guid.NewGuid(), PreferredName = "Alpha", CreatedAt = clock.Now.AddDays(-1) };
        db.CustomerProfiles.AddRange(zeta, alpha);
        void Booking(Guid customer, string status, int day)
        {
            db.Orders.Add(new Order { Id = Guid.NewGuid(), CustomerProfileId = customer, OrderNumber = "SORT-" + Guid.NewGuid().ToString("N")[..20],
                IdempotencyKey = Guid.NewGuid().ToString(), MealPlanTemplateId = order.MealPlanTemplateId, MealPlanPriceId = order.MealPlanPriceId,
                PlanName = "Balance", PlanDurationName = "1 day", StartDate = order.StartDate, EndDate = order.EndDate,
                Status = status, PaymentStatus = "PENDING", PlacedAt = clock.Now.AddDays(day), CreatedAt = clock.Now, UpdatedAt = clock.Now });
        }
        Booking(zeta.Id, "CONFIRMED", 1); Booking(zeta.Id, "ACTIVE", 2); Booking(zeta.Id, "COMPLETED", 3);
        for (var i = 0; i < 5; i++) Booking(alpha.Id, "CANCELLED", -5);
        await db.SaveChangesAsync();
        var most = await client.GetFromJsonAsync<AdminCustomerListResponse>("/api/v1/admin/customers?sort=booked_desc&pageSize=1");
        Assert.Equal(zeta.Id, Assert.Single(most!.Items).Id); Assert.Equal(3, most.Items[0].BookedPlans);
        var page2 = await client.GetFromJsonAsync<AdminCustomerListResponse>("/api/v1/admin/customers?sort=booked_desc&pageSize=1&pageNumber=2");
        Assert.NotEqual(zeta.Id, Assert.Single(page2!.Items).Id);
        foreach (var sort in new[] { "name_desc", "newest_desc", "latest_order_desc" })
        {
            var result = await client.GetFromJsonAsync<AdminCustomerListResponse>($"/api/v1/admin/customers?sort={sort}&search=Zeta&pageSize=1");
            Assert.Equal(zeta.Id, Assert.Single(result!.Items).Id);
        }
        var service = new AdminCustomerService(db, clock);
        Assert.Equal(alpha.Id, (await service.ListAsync(null, 1, 1, default, "name_asc")).Items[0].Id);
        Assert.Equal(zeta.Id, (await service.ListAsync(null, 1, 1, default, "latest_order_desc")).Items[0].Id);
        Assert.Equal(zeta.Id, (await service.ListAsync(null, 1, 1, default, "newest_desc")).Items[0].Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/admin/customers?sort=invalid")).StatusCode);
    }

    [UpcomingPostgresFact]
    public async Task Master_export_includes_all_pages_inactive_records_raw_translations_and_missing_labels()
    {
        var setup = await ApprovalClient(); await using var factory = setup.Factory; using var client = setup.Client;
        for (var i = 0; i < 105; i++)
            db.Ingredients.Add(new Ingredient { Id = Guid.NewGuid(), Code = $"EXPORT-{i:000}", IsActive = i % 2 == 0,
                CreatedAt = clock.Now, UpdatedAt = clock.Now,
                Translations = [new IngredientTranslation { Id = Guid.NewGuid(), LanguageCode = "en", Name = $"Ingredient {i}", Description = "Text with, comma and \"quote\"\nSecond line" },
                    new IngredientTranslation { Id = Guid.NewGuid(), LanguageCode = "ar", Name = $"مكون {i}", Description = "وصف عربي" }] });
        db.Ingredients.Add(new Ingredient { Id = Guid.NewGuid(), Code = "NO-AR", IsActive = false,
            Translations = [new IngredientTranslation { Id = Guid.NewGuid(), LanguageCode = "en", Name = "=SUM(A1)", Description = "English only" }] });
        await db.SaveChangesAsync();
        var before = await db.Ingredients.CountAsync();
        using var response = await client.GetAsync("/api/v1/admin/master-data/export");
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith(".csv", response.Content.Headers.ContentDisposition!.FileNameStar ?? response.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.AsSpan(0, 3).SequenceEqual(new byte[] { 239, 187, 191 }));
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("EXPORT-104", text); Assert.Contains("EXPORT-001", text); Assert.Contains("مكون 104", text);
        Assert.Contains("NO-AR", text); Assert.Contains("Arabic missing", text); Assert.Contains("'" + "=SUM(A1)", text);
        Assert.Contains("Text with, comma and \"\"quote\"\"\nSecond line", text);
        Assert.Contains("Meal categories", text); Assert.Contains("Meal types", text); Assert.Contains("Meals", text); Assert.Contains("Meal plans", text);
        Assert.Equal(before, await db.Ingredients.CountAsync());
        Assert.DoesNotContain(order.CustomerProfileId.ToString(), text);
        var directory = Environment.GetEnvironmentVariable("MASTER_EXPORT_PREVIEW_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "master-data-translations-sample.csv"), bytes);
        }
    }
}
