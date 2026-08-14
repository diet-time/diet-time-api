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

public sealed class KitchenPreparationReportTests
{
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
        Assert.Contains("KITCHEN PREPARATION REPORT", text);
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
        Assert.Contains("Page 1 of", text);
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

    private static KitchenPreparationPdfReportGenerator Generator() => new(
        new FixedTimeProvider(new DateTimeOffset(2026, 8, 14, 3, 30, 0, TimeSpan.Zero)),
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
