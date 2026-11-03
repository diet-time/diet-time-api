using System.Text;
using DietTime.Contracts;
using DietTime.Infrastructure;
using DietTime.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;

namespace DietTime.UnitTests;

public sealed partial class KitchenPreparationReportTests
{
    [Fact]
    public async Task Overview_is_first_and_customer_orders_keep_meals_in_separate_cards_without_selection_metadata()
    {
        var categories = new[] { "Breakfast", "Lunch", "Dinner", "AM Meal", "PM Meal", "Snack / Dessert" };
        var names = new[] { "Pancake", "Diet Time Beef Biryani", "Chicken Fajita", "Egg Roll", "Alfredo White Pasta", "Greek Yogurt With Granola" };
        var dishes = categories.Select((name, i) => new KitchenDishResponse(names[i], i == 5 ? "snack" : "meal", 1)
        { MealCategoryId = Guid.NewGuid(), MealCategoryName = name, MealCategoryDisplayOrder = i }).ToArray();
        var customer = Guid.NewGuid();
        var first = new KitchenSelectionResponse(Guid.NewGuid(), "ORD-1001", customer, "AUTO_FINALIZED", ["DEFAULT"], dishes, [], null)
        { CustomerName = "Sample Customer", MealPlanName = "Complete Balance", DeliveryAddress = "Building 25, Street 10, Doha", FoodPreferences = ["Seafood", "Beef", "Chicken"], Allergens = ["Celery", "Eggs", "Gluten cereals"] };
        var second = first with { ServiceDayId = Guid.NewGuid(), OrderReference = "ORD-1002", Dishes = [dishes[0], dishes[5]], FinalizationStatus = "CUSTOMER_CONFIRMED", FulfillmentMethod = "TAKEAWAY", TakeawayBranchName = "Azizia Branch", TakeawayBranchAddress = "Azizia, Doha" };
        var summary = new DeliveryPreparationSummaryResponse(new(2026, 11, 2), "Finalized", 2, 1, 8,
            dishes.Select((dish, i) => new DeliveryPreparationMealTypeResponse(dish.MealCategoryId!.Value, categories[i], i is 0 or 5 ? 2 : 1,
                [new(Guid.NewGuid(), names[i], i is 0 or 5 ? 2 : 1)])).ToArray(), [new(Guid.NewGuid(), "Complete Balance", 2)])
        { Selections = [first, second] };
        var pdf = await Generator(new DateTimeOffset(2026, 11, 2, 18, 49, 0, TimeSpan.Zero)).GenerateAsync(summary, default);
        var directory = Environment.GetEnvironmentVariable("KITCHEN_REPORT_PREVIEW_DIR");
        if (!string.IsNullOrWhiteSpace(directory)) { Directory.CreateDirectory(directory); await File.WriteAllBytesAsync(Path.Combine(directory, "kitchen-preparation-sample.pdf"), pdf); }
        using var document = PdfReader.Open(new MemoryStream(pdf));
        Assert.True(document.PageCount >= 2);
        foreach (PdfSharp.Pdf.PdfPage page in document.Pages)
        {
            Assert.Contains("DIET TIME", ExtractText(page));
            var images = page.Resources.Elements.GetDictionary("/XObject");
            Assert.NotNull(images);
            Assert.Contains(images.Elements.Values, value =>
                value is PdfSharp.Pdf.Advanced.PdfReference reference &&
                reference.Value is PdfSharp.Pdf.PdfDictionary image &&
                image.Elements.GetName("/Subtype") == "/Image");
        }
        var overview = ExtractText(document.Pages[0]); var packing = string.Join(" ", document.Pages.Cast<PdfSharp.Pdf.PdfPage>().Skip(1).Select(ExtractText));
        Assert.Contains("PREPARATION OVERVIEW", overview); Assert.Contains("TOTAL MEAL ITEMS TO PREPARE", overview);
        Assert.DoesNotContain("Sample Customer", overview);
        Assert.Contains("Customer Orders", packing); Assert.Contains("Sample Customer", packing);
        var compactPacking = string.Concat(packing.Where(c => !char.IsWhiteSpace(c)));
        Assert.Contains("ORD-1001", compactPacking); Assert.Contains("ORD-1002", compactPacking);
        Assert.DoesNotContain("Selections:", packing);
        Assert.DoesNotContain("Automatically assigned", packing);
        Assert.Contains("MEALS FOR THIS ORDER", packing);
        Assert.Contains("Allergens: Celery, Eggs, Gluten cereals", packing);
        Assert.DoesNotContain("AUTO_FINALIZED", packing); Assert.DoesNotContain("DEFAULT", packing);
        Assert.Contains("Azizia Branch", packing); Assert.Contains("Building 25, Street 10, Doha", packing);
        foreach (var name in names) Assert.Contains(name, packing);
    }

    [Fact]
    public async Task Long_customer_order_flows_across_pages_without_losing_dishes_or_allergy_notes()
    {
        var dishes = Enumerable.Range(1, 70).Select(i => new KitchenDishResponse($"Customer dish {i:000} with a long descriptive kitchen name", "meal", 1)
        { MealCategoryName = "Lunch", MealCategoryDisplayOrder = 1 }).ToArray();
        var summary = new DeliveryPreparationSummaryResponse(new(2026, 11, 2), "Preliminary", 1, 1, 70,
            [new(Guid.NewGuid(), "Lunch", 70, [new(Guid.NewGuid(), "Bulk preparation", 70)])], [new(Guid.NewGuid(), "Balance", 1)])
        { IsPreliminary = true, Selections = [new(Guid.NewGuid(), "ORD-LONG", Guid.NewGuid(), "AUTO_FINALIZED", ["DEFAULT"], dishes, [], null)
            { CustomerName = "Long order customer", DeliveryAddress = "Long delivery address in Doha", Allergens = ["Eggs (Avoid traces: shared utensils)"] }] };
        var pdf = await Generator().GenerateAsync(summary, default);
        using var document = PdfReader.Open(new MemoryStream(pdf));
        Assert.True(document.PageCount > 2);
        var text = ExtractText(document);
        Assert.Contains("Customer dish 001", text); Assert.Contains("Customer dish 070", text);
        Assert.Contains("Avoid traces: shared utensils", text); Assert.Contains("MEAL ITEMS IN THIS ORDER", text);
        Assert.Contains("PRELIMINARY", text);
    }
    [Fact]
    public async Task Pdf_preserves_order_numbers_in_preparation_review_warnings()
    {
        var summary = new DeliveryPreparationSummaryResponse(new(2026, 11, 3), "NeedsReview", 1, 1, 0, [], [])
        {
            IsPreliminary = true,
            OperationalExceptions = ["Order ORD-20261102-000085: Not enough eligible meal types."]
        };
        var text = ExtractText(await Generator().GenerateAsync(summary, default));
        Assert.Contains("ORD-20261102-000085", string.Concat(text.Where(c => !char.IsWhiteSpace(c))));
        Assert.Contains("Not enough eligible meal types.", text);
    }
    [Theory]
    [InlineData("HIGH_PROTEIN", "High Protein")]
    [InlineData("SEAFOOD", "Seafood")]
    [InlineData("ALLERGEN: EGGS", "Allergen: Eggs")]
    [InlineData("LOW_CARB", "Low Carb")]
    [InlineData("Allergen: Hen eggs", "Allergen: Hen eggs")]
    public void Dietary_codes_have_readable_fallback_labels(string code, string expected) =>
        Assert.Equal(expected, DietTime.Application.PreparationDisplayText.Label(code));

    [Fact]
    public async Task Pdf_uses_configured_categories_for_the_same_dish_and_separates_dietary_groups()
    {
        var breakfast = Guid.NewGuid(); var lunch = Guid.NewGuid(); var dish = Guid.NewGuid();
        var summary = DietTime.Application.DeliveryPreparationAggregation.Build(new(2026, 11, 2),
            [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Balanced")], [
                new(Guid.NewGuid(), breakfast, "Early Breakfast", 1, dish, "Shared dish", 1),
                new(Guid.NewGuid(), lunch, "Late Lunch", 2, dish, "Shared dish", 2)]) with {
            Selections = [new KitchenSelectionResponse(Guid.NewGuid(), "ORD-CATEGORY", Guid.NewGuid(), "AUTO_FINALIZED", ["DEFAULT"], [
                new("Shared dish", "meal", 1) { MealCategoryId = breakfast, MealCategoryName = "Early Breakfast", MealCategoryDisplayOrder = 1 },
                new("Shared dish", "meal", 2) { MealCategoryId = lunch, MealCategoryName = "Late Lunch", MealCategoryDisplayOrder = 2 }], [], null) {
                    FoodPreferences = ["Seafood"], Allergens = ["Celery", "Eggs (including traces)"] }]
        };
        var text = ExtractText(await Generator().GenerateAsync(summary, default));
        Assert.Contains("Early Breakfast", text); Assert.Contains("Late Lunch", text);
        Assert.Contains("Shared dish", text); Assert.Contains("Qty", text);
        Assert.Contains("Food Preferences: Seafood", text);
        Assert.Contains("Allergens: Celery, Eggs (including traces)", text);
        Assert.DoesNotContain("(meal)", text); Assert.DoesNotContain("Allergen:", text);
        Assert.Equal(3, summary.MealItemCount);
    }

    [Fact]
    public void Dietary_groups_preserve_notes_deduplicate_and_prefer_structured_values()
    {
        var legacy = DietTime.Application.PreparationDisplayText.Dietary(null, null,
            ["Seafood", "Allergen: Celery", "ALLERGEN: celery", "Allergen: Eggs (including traces)", "Avoid sauces: spicy"]);
        Assert.Equal(new[] { "Seafood", "Avoid sauces: spicy" }, legacy.Preferences);
        Assert.Equal(new[] { "Celery", "Eggs (including traces)" }, legacy.Allergens);
        var structured = DietTime.Application.PreparationDisplayText.Dietary([], ["Hen eggs", "hen eggs"], ["Allergen: obsolete"]);
        Assert.Empty(structured.Preferences);
        Assert.Equal(new[] { "Hen eggs" }, structured.Allergens);
        Assert.Equal("Eggs (Avoid traces: shared utensils)", DietTime.Application.PreparationDisplayText.AllergenLabel("Allergen: Eggs", "Avoid traces: shared utensils"));
    }

    [Fact]
    public async Task Pdf_includes_customer_names_home_addresses_and_friendly_dietary_labels()
    {
        var customerId = Guid.NewGuid();
        var summary = new DeliveryPreparationSummaryResponse(new(2026, 8, 16), "Preliminary", 2, 1, 0, [], [])
        {
            IsPreliminary = true,
            Selections = [
                new KitchenSelectionResponse(Guid.NewGuid(), "ORD-1", customerId, "UNFINALIZED", [], [], ["HIGH_PROTEIN", "ALLERGEN: EGGS"], null)
                { CustomerName = "Biju Manayagath", DeliveryAddress = "Building 10, Street 20, Zone 30, Doha",
                    DietaryRestrictionLabels = ["High Protein", "Allergen: Hen eggs"] },
                new KitchenSelectionResponse(Guid.NewGuid(), "ORD-2", customerId, "UNFINALIZED", [], [], [], null,
                    "TAKEAWAY", "Azizia branch") { CustomerName = "Biju Manayagath", DeliveryAddress = "Must not display for takeaway" }
            ]
        };
        var text = ExtractText(await Generator().GenerateAsync(summary, default));
        Assert.Contains("Biju Manayagath", text);
        Assert.Contains("Building 10, Street 20, Zone 30, Doha", text);
        Assert.Contains("Food Preferences: High Protein", text);
        Assert.Contains("Allergens: Hen eggs", text);
        Assert.DoesNotContain("Allergen:", text);
        Assert.Contains("Azizia branch", text);
        Assert.DoesNotContain(customerId.ToString(), text);
        Assert.DoesNotContain("Must not display for takeaway", text);
        Assert.DoesNotContain("HIGH_PROTEIN", text);
    }

    [Fact]
    public async Task Report_contains_summary_menu_totals_plans_and_plural_labels()
    {
        var breakfastId = Guid.NewGuid();
        var lunchId = Guid.NewGuid();
        var summary = new DeliveryPreparationSummaryResponse(
            new(2026, 8, 16), "Scheduled", 2, 1, 8,
            [
                new(breakfastId, "Breakfast", 5,
                [
                    new(Guid.NewGuid(), "Oatmeal Banana", 3),
                    new(Guid.NewGuid(), "Hot & Cheesy Scrambled Egg Croissant", 2)
                ]),
                new(lunchId, "Lunch", 3,
                [new(Guid.NewGuid(), "Creamy Spinach With Rice", 3)])
            ],
            [
                new(Guid.NewGuid(), "Balanced Living", 1),
                new(Guid.NewGuid(), "Everyday Choice", 2)
            ]);

        var pdf = await Generator().GenerateAsync(summary, default);
        var text = ExtractText(pdf);

        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
        Assert.Contains("Kitchen Preparation", text);
        Assert.Contains("Sunday, 16 August 2026", text);
        Assert.Contains("Oatmeal Banana", text);
        Assert.Contains("Hot & Cheesy Scrambled Egg Croissant", text);
        Assert.Contains("Balanced Living", text);
        Assert.Contains("1 order", text);
        Assert.Contains("2 orders", text);
        Assert.Contains("Across 2 orders and 1 customer", text);
        Assert.DoesNotContain("Special", text);
        Assert.DoesNotContain("Details", text);
    }

    [Fact]
    public async Task Empty_day_generates_valid_empty_state_pdf()
    {
        var pdf = await Generator().GenerateAsync(
            new(new(2026, 8, 14), "NoDeliveries", 0, 0, 0, [], []), default);
        var text = ExtractText(pdf);

        Assert.Contains("NO PREPARATION REQUIRED", text);
        Assert.Contains("There are no meals scheduled", text);
        Assert.Contains("Friday, 14 August 2026", text);
    }

    [Fact]
    public async Task Standard_day_uses_single_page_landscape_dashboard_layout()
    {
        static DeliveryPreparationMealTypeResponse MealType(string name, params (string Name, int Qty)[] items) =>
            new(
                Guid.NewGuid(),
                name,
                items.Sum(item => item.Qty),
                items.Select(item => new DeliveryPreparationMenuItemResponse(
                    Guid.NewGuid(), item.Name, item.Qty)).ToArray());

        var summary = new DeliveryPreparationSummaryResponse(
            new(2026, 8, 15), "Scheduled", 7, 2, 28,
            [
                MealType("Breakfast", ("Hot & Cheesy Scrambled Egg Croissant", 5), ("Oatmeal Banana", 2)),
                MealType("Lunch", ("Butter Shrimp With Rice", 5), ("Shrimp Risotto", 2)),
                MealType("Dinner", ("Fattoush Salad", 7)),
                MealType("Snack / Dessert", ("Mushroom Soup", 7))
            ],
            [
                new(Guid.NewGuid(), "Everyday Choice", 4),
                new(Guid.NewGuid(), "Balanced Living", 2),
                new(Guid.NewGuid(), "Complete Balance", 1)
            ]);

        var pdf = await Generator().GenerateAsync(summary, default);
        using var document = PdfReader.Open(new MemoryStream(pdf));
        var text = ExtractText(document);

        Assert.True(
            document.PageCount == 1,
            string.Join(" | ", document.Pages.Cast<PdfSharp.Pdf.PdfPage>()
                .Select((page, index) => $"Page {index + 1}: {ExtractText(page)}")));
        Assert.True(document.Pages[0].Width.Point > document.Pages[0].Height.Point);
        Assert.Contains("PREPARATION OVERVIEW", text);
        Assert.Contains("MENU ITEMS TO PREPARE", text);
        Assert.Contains("PLAN BREAKDOWN", text);
        Assert.Contains("TOTAL MEAL ITEMS TO PREPARE", text);
        Assert.Contains("Snack / Dessert", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Large_report_spans_pages_and_keeps_long_item_names()
    {
        var items = Enumerable.Range(1, 150)
            .Select(index => new DeliveryPreparationMenuItemResponse(
                Guid.NewGuid(),
                $"Menu item {index:000} with a deliberately long operational kitchen name",
                index))
            .ToArray();
        var total = items.Sum(item => item.Quantity);
        var summary = new DeliveryPreparationSummaryResponse(
            new(2026, 8, 16), "Scheduled", 1, 1, total,
            [new(Guid.NewGuid(), "Breakfast", total, items)],
            [new(Guid.NewGuid(), "Balanced Living", 1)]);

        var pdf = await Generator().GenerateAsync(summary, default);
        using var document = PdfReader.Open(new MemoryStream(pdf));
        var text = ExtractText(document);

        Assert.True(document.PageCount > 1);
        Assert.Contains("Menu item 001 with a deliberately long operational kitchen name", text);
        Assert.Contains("Menu item 150 with a deliberately long operational kitchen name", text);
        Assert.Contains("Page 1", text);
    }

    [Fact]
    public async Task Report_contract_cannot_expose_customer_pii()
    {
        var publicProperties = typeof(DeliveryPreparationSummaryResponse)
            .GetProperties()
            .Select(property => property.Name)
            .Concat(typeof(DeliveryPreparationMealTypeResponse).GetProperties().Select(property => property.Name))
            .Concat(typeof(DeliveryPreparationMenuItemResponse).GetProperties().Select(property => property.Name))
            .Concat(typeof(DeliveryPreparationPlanResponse).GetProperties().Select(property => property.Name))
            .ToArray();

        Assert.DoesNotContain(publicProperties, name =>
            name.Contains("CustomerName", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Mobile", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Address", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Email", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("DateOfBirth", StringComparison.OrdinalIgnoreCase));

        var pdf = await Generator().GenerateAsync(
            new(new(2026, 8, 16), "Scheduled", 1, 1, 1,
                [new(Guid.NewGuid(), "Breakfast", 1,
                    [new(Guid.NewGuid(), "Safe Meal", 1)])],
                [new(Guid.NewGuid(), "Safe Plan", 1)]), default);
        Assert.DoesNotContain("customer@example.com", ExtractText(pdf), StringComparison.OrdinalIgnoreCase);
    }

    private static KitchenPreparationPdfReportGenerator Generator(DateTimeOffset? now = null) => new(
        new FixedTimeProvider(now ?? new DateTimeOffset(2026, 8, 14, 3, 30, 0, TimeSpan.Zero)),
        Options.Create(new OperationsDashboardOptions { BusinessTimeZone = "Asia/Qatar" }),
        NullLogger<KitchenPreparationPdfReportGenerator>.Instance);

    private static string ExtractText(byte[] pdf)
    {
        using var document = PdfReader.Open(new MemoryStream(pdf));
        return ExtractText(document);
    }

    private static string ExtractText(PdfSharp.Pdf.PdfDocument document)
    {
        var text = new StringBuilder();
        foreach (var page in document.Pages)
            Append(ContentReader.ReadContent(page), text);
        return text.ToString();
    }

    private static string ExtractText(PdfSharp.Pdf.PdfPage page)
    {
        var text = new StringBuilder();
        Append(ContentReader.ReadContent(page), text);
        return text.ToString();
    }

    private static void Append(CObject value, StringBuilder text)
    {
        switch (value)
        {
            case CString textValue:
                text.Append(textValue.Value).Append(' ');
                break;
            case COperator operation:
                foreach (var operand in operation.Operands)
                    Append(operand, text);
                break;
            case CSequence sequence:
                foreach (var item in sequence)
                    Append(item, text);
                break;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
