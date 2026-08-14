using System.Globalization;
using DietTime.Application;
using DietTime.Contracts;
using DietTime.Persistence;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PdfSharp.Fonts;

namespace DietTime.Infrastructure;

public sealed class KitchenPreparationPdfReportGenerator : IKitchenPreparationReportGenerator
{
    private const string FontFamily = "DietTimeSans";
    private static readonly Color DarkGreen = Color.Parse("#174D3A");
    private static readonly Color LightGreen = Color.Parse("#EAF3EE");
    private static readonly Color Border = Color.Parse("#B8C7BF");
    private static readonly Color Text = Color.Parse("#26332D");
    private static readonly object FontResolverLock = new();
    private readonly TimeProvider timeProvider;
    private readonly TimeZoneInfo businessTimeZone;
    private readonly ILogger<KitchenPreparationPdfReportGenerator> logger;

    public KitchenPreparationPdfReportGenerator(
        TimeProvider timeProvider,
        IOptions<OperationsDashboardOptions> options,
        ILogger<KitchenPreparationPdfReportGenerator> logger)
    {
        this.timeProvider = timeProvider;
        businessTimeZone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.BusinessTimeZone);
        this.logger = logger;
        EnsureFontResolver();
    }

    public Task<byte[]> GenerateAsync(
        DeliveryPreparationSummaryResponse summary,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(summary);
        cancellationToken.ThrowIfCancellationRequested();

        var document = BuildDocument(summary);
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        cancellationToken.ThrowIfCancellationRequested();

        using var output = new MemoryStream();
        renderer.PdfDocument.Save(output, false);
        return Task.FromResult(output.ToArray());
    }

    private Document BuildDocument(DeliveryPreparationSummaryResponse summary)
    {
        var document = new Document();
        document.Info.Title = $"Kitchen Preparation - {summary.Date:yyyy-MM-dd}";
        document.Info.Author = "Diet Time";
        document.Info.Subject = "Kitchen preparation quantities";
        ConfigureStyles(document);

        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.Orientation = Orientation.Portrait;
        section.PageSetup.LeftMargin = Unit.FromMillimeter(18);
        section.PageSetup.RightMargin = Unit.FromMillimeter(18);
        section.PageSetup.TopMargin = Unit.FromMillimeter(16);
        section.PageSetup.BottomMargin = Unit.FromMillimeter(18);
        AddFooter(section, summary.Date);
        AddReportHeader(section, summary.Date);

        if (summary.MealItemCount == 0 && summary.MealTypes.Count == 0)
            AddEmptyState(section, summary);
        else
            AddPreparationReport(section, summary);

        return document;
    }

    private void AddReportHeader(Section section, DateOnly deliveryDate)
    {
        var brand = section.AddParagraph("DIET TIME");
        brand.Style = "Brand";
        brand.Format.SpaceAfter = Unit.FromMillimeter(1);

        var title = section.AddParagraph("KITCHEN PREPARATION REPORT");
        title.Style = "ReportTitle";

        var delivery = section.AddParagraph(
            deliveryDate.ToString("dddd, d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB")));
        delivery.Style = "DeliveryDate";

        var generatedAt = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), businessTimeZone);
        var generated = section.AddParagraph(
            $"Generated: {generatedAt.ToString("dd MMM yyyy, hh:mm tt", CultureInfo.GetCultureInfo("en-US"))}");
        generated.Style = "Generated";
        generated.Format.SpaceAfter = Unit.FromMillimeter(7);
    }

    private static void AddEmptyState(Section section, DeliveryPreparationSummaryResponse summary)
    {
        var heading = section.AddParagraph("NO PREPARATION REQUIRED");
        heading.Style = "EmptyHeading";
        heading.Format.SpaceAfter = Unit.FromMillimeter(4);

        var message = section.AddParagraph(
            "There are no meals scheduled for preparation on this delivery date.");
        message.Format.SpaceAfter = Unit.FromMillimeter(6);

        AddKeyValueTable(section,
        [
            ("Orders", summary.OrderCount.ToString(CultureInfo.InvariantCulture)),
            ("Customers", summary.CustomerCount.ToString(CultureInfo.InvariantCulture)),
            ("Meal Items", summary.MealItemCount.ToString(CultureInfo.InvariantCulture))
        ]);
    }

    private void AddPreparationReport(Section section, DeliveryPreparationSummaryResponse summary)
    {
        AddSectionHeading(section, "SUMMARY");
        AddKeyValueTable(section,
        [
            ("Orders", summary.OrderCount.ToString(CultureInfo.InvariantCulture)),
            ("Customers", summary.CustomerCount.ToString(CultureInfo.InvariantCulture)),
            ("Total Meal Items", summary.MealItemCount.ToString(CultureInfo.InvariantCulture))
        ]);

        AddSectionHeading(section, "PREPARATION OVERVIEW");
        AddKeyValueTable(section, summary.MealTypes
            .Select(type => (type.MealTypeName, type.Quantity.ToString(CultureInfo.InvariantCulture)))
            .ToArray());

        AddSectionHeading(section, "MENU ITEMS TO PREPARE");
        foreach (var mealType in summary.MealTypes)
            AddMealType(section, summary.Date, mealType);

        AddSectionHeading(section, "PLAN BREAKDOWN");
        AddKeyValueTable(section, summary.PlanBreakdown
            .Select(plan => (
                plan.MealPlanName,
                $"{plan.OrderCount} {Pluralize(plan.OrderCount, "order", "orders")}"))
            .ToArray());

        var totalBox = section.AddTable();
        totalBox.KeepTogether = true;
        totalBox.Borders.Width = Unit.FromPoint(0.8);
        totalBox.Borders.Color = DarkGreen;
        totalBox.Shading.Color = LightGreen;
        totalBox.AddColumn(Unit.FromMillimeter(174));
        var cell = totalBox.AddRow().Cells[0];
        cell.Format.Alignment = ParagraphAlignment.Center;
        cell.Format.SpaceBefore = Unit.FromMillimeter(5);
        cell.Format.SpaceAfter = Unit.FromMillimeter(5);
        var label = cell.AddParagraph("TOTAL MEAL ITEMS TO PREPARE");
        label.Format.Font.Bold = true;
        label.Format.Font.Color = DarkGreen;
        label.Format.Font.Size = Unit.FromPoint(11);
        var amount = cell.AddParagraph(summary.MealItemCount.ToString(CultureInfo.InvariantCulture));
        amount.Format.Font.Bold = true;
        amount.Format.Font.Color = DarkGreen;
        amount.Format.Font.Size = Unit.FromPoint(24);
        var detail = cell.AddParagraph(
            $"Across {summary.OrderCount} {Pluralize(summary.OrderCount, "order", "orders")} and " +
            $"{summary.CustomerCount} {Pluralize(summary.CustomerCount, "customer", "customers")}");
        detail.Format.Font.Size = Unit.FromPoint(9);
    }

    private void AddMealType(
        Section section,
        DateOnly deliveryDate,
        DeliveryPreparationMealTypeResponse mealType)
    {
        var itemTotal = mealType.Items.Sum(item => item.Quantity);
        if (itemTotal != mealType.Quantity)
        {
            logger.LogWarning(
                "Kitchen preparation source totals are inconsistent. DeliveryDate={DeliveryDate} MealTypeId={MealTypeId} MealType={MealType} SourceQuantity={SourceQuantity} ItemQuantity={ItemQuantity}",
                deliveryDate, mealType.MealTypeId, mealType.MealTypeName,
                mealType.Quantity, itemTotal);
        }

        var heading = section.AddParagraph(mealType.MealTypeName.ToUpperInvariant());
        heading.Style = "MealTypeHeading";
        heading.Format.KeepWithNext = true;

        var table = section.AddTable();
        table.Format.SpaceAfter = Unit.FromMillimeter(6);
        table.Borders.Color = Border;
        table.Borders.Width = Unit.FromPoint(0.35);
        table.AddColumn(Unit.FromMillimeter(145));
        table.AddColumn(Unit.FromMillimeter(29));

        var header = table.AddRow();
        header.HeadingFormat = true;
        header.Format.Font.Bold = true;
        header.Shading.Color = LightGreen;
        header.Cells[0].AddParagraph("Menu Item");
        header.Cells[1].AddParagraph("Quantity");
        header.Cells[1].Format.Alignment = ParagraphAlignment.Right;

        foreach (var item in mealType.Items)
        {
            var row = table.AddRow();
            row.Cells[0].AddParagraph(item.MenuItemName);
            row.Cells[1].AddParagraph(item.Quantity.ToString(CultureInfo.InvariantCulture));
            row.Cells[1].Format.Alignment = ParagraphAlignment.Right;
        }

        var total = table.AddRow();
        total.Format.Font.Bold = true;
        total.Borders.Top.Width = Unit.FromPoint(0.8);
        total.Cells[0].AddParagraph("TOTAL");
        total.Cells[1].AddParagraph(mealType.Quantity.ToString(CultureInfo.InvariantCulture));
        total.Cells[1].Format.Alignment = ParagraphAlignment.Right;
    }

    private static void AddSectionHeading(Section section, string text)
    {
        var paragraph = section.AddParagraph(text);
        paragraph.Style = "SectionHeading";
        paragraph.Format.KeepWithNext = true;
    }

    private static void AddKeyValueTable(Section section, IReadOnlyCollection<(string Label, string Value)> rows)
    {
        var table = section.AddTable();
        table.KeepTogether = true;
        table.Format.SpaceAfter = Unit.FromMillimeter(7);
        table.Borders.Color = Border;
        table.Borders.Width = Unit.FromPoint(0.35);
        table.AddColumn(Unit.FromMillimeter(145));
        table.AddColumn(Unit.FromMillimeter(29));
        foreach (var (label, value) in rows)
        {
            var row = table.AddRow();
            row.Cells[0].AddParagraph(label);
            row.Cells[1].AddParagraph(value);
            row.Cells[1].Format.Alignment = ParagraphAlignment.Right;
            row.Cells[1].Format.Font.Bold = true;
        }
    }

    private static void AddFooter(Section section, DateOnly deliveryDate)
    {
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = Unit.FromPoint(8);
        footer.Format.Font.Color = Colors.DimGray;
        footer.Format.Alignment = ParagraphAlignment.Center;
        footer.AddText("Diet Time - Kitchen Preparation | ");
        footer.AddText(deliveryDate.ToString("dd MMM yyyy", CultureInfo.GetCultureInfo("en-GB")));
        footer.AddText(" | Page ");
        footer.AddPageField();
        footer.AddText(" of ");
        footer.AddNumPagesField();
    }

    private static void ConfigureStyles(Document document)
    {
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = FontFamily;
        normal.Font.Size = Unit.FromPoint(9.5);
        normal.Font.Color = Text;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);

        AddStyle(document, "Brand", 14, true, DarkGreen);
        AddStyle(document, "ReportTitle", 17, true, DarkGreen);
        AddStyle(document, "DeliveryDate", 15, true, Text);
        AddStyle(document, "Generated", 8, false, Colors.DimGray);
        var sectionHeading = AddStyle(document, "SectionHeading", 10, true, DarkGreen);
        sectionHeading.ParagraphFormat.SpaceBefore = Unit.FromMillimeter(4);
        sectionHeading.ParagraphFormat.SpaceAfter = Unit.FromMillimeter(2);
        var mealHeading = AddStyle(document, "MealTypeHeading", 10, true, Colors.White);
        mealHeading.ParagraphFormat.Shading.Color = DarkGreen;
        mealHeading.ParagraphFormat.LeftIndent = Unit.FromMillimeter(2);
        mealHeading.ParagraphFormat.SpaceBefore = Unit.FromMillimeter(2);
        mealHeading.ParagraphFormat.SpaceAfter = Unit.FromMillimeter(1.5);
        var empty = AddStyle(document, "EmptyHeading", 13, true, DarkGreen);
        empty.ParagraphFormat.Shading.Color = LightGreen;
        empty.ParagraphFormat.LeftIndent = Unit.FromMillimeter(3);
        empty.ParagraphFormat.SpaceBefore = Unit.FromMillimeter(8);
    }

    private static Style AddStyle(
        Document document, string name, double size, bool bold, Color color)
    {
        var style = document.Styles.AddStyle(name, StyleNames.Normal);
        style.Font.Name = FontFamily;
        style.Font.Size = Unit.FromPoint(size);
        style.Font.Bold = bold;
        style.Font.Color = color;
        return style;
    }

    private static string Pluralize(int count, string singular, string plural) =>
        count == 1 ? singular : plural;

    private static void EnsureFontResolver()
    {
        lock (FontResolverLock)
        {
            if (GlobalFontSettings.FontResolver is null)
                GlobalFontSettings.FontResolver = new DietTimeFontResolver();
        }
    }
}

internal sealed class DietTimeFontResolver : IFontResolver
{
    private const string Regular = "DietTimeSans-Regular";
    private const string Bold = "DietTimeSans-Bold";
    private static readonly Lazy<byte[]> RegularFont = new(() => ReadFont(false));
    private static readonly Lazy<byte[]> BoldFont = new(() => ReadFont(true));

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(isBold ? Bold : Regular, false, isItalic);

    public byte[]? GetFont(string faceName) => faceName switch
    {
        Bold => BoldFont.Value,
        Regular => RegularFont.Value,
        _ => null
    };

    private static byte[] ReadFont(bool bold)
    {
        var windowsFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var candidates = bold
            ? new[]
            {
                Path.Combine(windowsFonts, "arialbd.ttf"),
                "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
                "/usr/share/fonts/dejavu/DejaVuSans-Bold.ttf"
            }
            : new[]
            {
                Path.Combine(windowsFonts, "arial.ttf"),
                "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                "/usr/share/fonts/dejavu/DejaVuSans.ttf"
            };

        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
            throw new InvalidOperationException(
                "No supported report font was found. Install Arial or DejaVu Sans.");
        return File.ReadAllBytes(path);
    }
}
