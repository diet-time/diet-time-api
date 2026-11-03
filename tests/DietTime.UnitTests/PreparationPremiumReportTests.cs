using DietTime.Contracts;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;

namespace DietTime.UnitTests;

public sealed partial class KitchenPreparationReportTests
{
    [Fact]
    public async Task Customer_groups_preserve_each_orders_location_meals_allergens_and_maps_across_pages()
    {
        var alpha = Guid.NewGuid(); var beta = Guid.NewGuid();
        var selections = new List<KitchenSelectionResponse>();
        var categories = new[] { "Breakfast", "Lunch", "Dinner", "AM Meal", "PM Meal", "Snack / Dessert" };
        for (var i = 0; i < 6; i++)
        {
            var dishes = categories.Select((category, index) => new KitchenDishResponse($"Order {i + 1} dish for {category}", "meal", index == 0 ? 2 : 1)
                { MealCategoryName = category, MealCategoryDisplayOrder = index }).ToArray();
            var pickup = i is 1 or 4;
            selections.Add(new(Guid.NewGuid(), $"ORD-DEMO-{i + 1:000}", i is 0 or 2 ? alpha : i == 1 ? beta : Guid.NewGuid(), "AUTO_FINALIZED", ["DEFAULT"], dishes, [], null,
                pickup ? "TAKEAWAY" : "HOME_DELIVERY", pickup ? "Diet Time - Azizia" : null, pickup ? "Building 18, Street 210, Zone 55, Azizia, Doha" : null)
            {
                CustomerName = i is 0 or 2 ? "Aisha Al Mansouri" : i == 1 ? "Biju Menon" : $"Customer {i + 1}",
                MealPlanName = "Complete Balance", FoodPreferences = ["Seafood", "Chicken"],
                Allergens = i is 0 or 2 ? ["Eggs (Avoid traces: shared utensils)", "Celery", "Gluten cereals"] : ["Peanuts"],
                DeliveryAddress = i == 5 ? "Pinned location, Qatar" : $"Building: {25 + i} · Street: 10 · Unit: 4B · Zone: 45 · Area: Al Sadd, Doha · Landmark / directions: Opposite the community park, use the side entrance near the pharmacy; call on arrival at the security gate for access to the residential courtyard",
                DeliveryLatitude = pickup ? null : 25.2854m, DeliveryLongitude = pickup ? null : 51.5310m
            });
        }
        // Deliberately interleave two orders belonging to one customer.
        var summary = new DeliveryPreparationSummaryResponse(new(2026, 11, 3), "Finalized", 6, 5, 42,
            categories.Select((category, i) => new DeliveryPreparationMealTypeResponse(Guid.NewGuid(), category, i == 0 ? 12 : 6,
                [new(Guid.NewGuid(), $"Kitchen batch - {category}", i == 0 ? 12 : 6)])).ToArray(),
            [new(Guid.NewGuid(), "Complete Balance", 6)])
        { Selections = selections, DeliveryOrderCount = 4, TakeawayOrderCount = 2 };
        var pdf = await Generator(new DateTimeOffset(2026, 11, 3, 7, 30, 0, TimeSpan.Zero)).GenerateAsync(summary, default);
        using var document = PdfReader.Open(new MemoryStream(pdf));
        Assert.True(document.PageCount >= 4);
        var text = ExtractText(document);
        var compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        Assert.True(compact.IndexOf("ORD-DEMO-001", StringComparison.Ordinal) < compact.IndexOf("ORD-DEMO-003", StringComparison.Ordinal));
        Assert.True(compact.IndexOf("ORD-DEMO-003", StringComparison.Ordinal) < compact.IndexOf("ORD-DEMO-002", StringComparison.Ordinal));
        foreach (var selection in selections)
        {
            Assert.Contains(selection.OrderReference, compact);
            foreach (var dish in selection.Dishes) Assert.Contains(dish.MealName, text);
        }
        Assert.Contains("DELIVERY LOCATION", text); Assert.Contains("COLLECTION LOCATION", text);
        Assert.Contains("Address verification required", text); Assert.DoesNotContain("Pinned location", text);
        Assert.Contains("Avoid traces: shared utensils", text); Assert.Contains("security gate", text);
        Assert.Contains("Diet Time - Azizia", text); Assert.Contains("25.2854", text);
        Assert.Contains("DELIVERIES", text); Assert.Contains("TAKEAWAYS", text);
        Assert.DoesNotContain("AUTO_FINALIZED", text); Assert.DoesNotContain("DEFAULT", text);
        Assert.DoesNotContain("Status:", text); Assert.DoesNotContain("Finalized", text);
        Assert.Contains(document.Pages.Cast<PdfSharp.Pdf.PdfPage>(), page => page.Elements.ContainsKey("/Annots"));
        Assert.All(document.Pages.Cast<PdfSharp.Pdf.PdfPage>(), page =>
        {
            Assert.Contains("DIET TIME", ExtractText(page));
            Assert.True(page.Width.Point > page.Height.Point);
        });
        var directory = Environment.GetEnvironmentVariable("KITCHEN_REPORT_PREVIEW_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "kitchen-preparation-customer-report.pdf"), pdf);
        }
    }
}
