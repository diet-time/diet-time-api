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
    private const double ContentWidthMm = 267;
    private static readonly Color Green = Color.Parse("#0B5D4B");
    private static readonly Color DarkGreen = Color.Parse("#173B34");
    private static readonly Color LightGreen = Color.Parse("#E9F3EF");
    private static readonly Color LightGray = Color.Parse("#F6F8F7");
    private static readonly Color Border = Color.Parse("#D8E2DE");
    private static readonly Color Muted = Color.Parse("#667B75");
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
        section.PageSetup.Orientation = Orientation.Landscape;
        section.PageSetup.LeftMargin = Unit.FromMillimeter(15);
        section.PageSetup.RightMargin = Unit.FromMillimeter(15);
        section.PageSetup.TopMargin = Unit.FromMillimeter(9);
        section.PageSetup.BottomMargin = Unit.FromMillimeter(13);
        section.PageSetup.FooterDistance = Unit.FromMillimeter(7);
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
        brand.Format.SpaceAfter = Unit.FromMillimeter(2);

        var title = section.AddParagraph("Kitchen Preparation");
        title.Style = "ReportTitle";

        var delivery = section.AddParagraph(
            deliveryDate.ToString("dddd, d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB")));
        delivery.Style = "DeliveryDate";

        var generatedAt = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), businessTimeZone);
        var generated = section.AddParagraph(
            $"Generated {generatedAt.ToString("dd MMM yyyy, hh:mm tt", CultureInfo.GetCultureInfo("en-US"))}");
        generated.Style = "Generated";
        generated.Format.SpaceAfter = Unit.FromMillimeter(2);
    }

    private static void AddEmptyState(Section section, DeliveryPreparationSummaryResponse summary)
    {
        var heading = section.AddParagraph("NO PREPARATION REQUIRED");
        heading.Style = "EmptyHeading";
        heading.Format.SpaceAfter = Unit.FromMillimeter(4);

        var message = section.AddParagraph(
            "There are no meals scheduled for preparation on this delivery date.");
        message.Format.SpaceAfter = Unit.FromMillimeter(6);

        AddMetricCards(section,
        [
            ("Orders", summary.OrderCount.ToString(CultureInfo.InvariantCulture)),
            ("Customers", summary.CustomerCount.ToString(CultureInfo.InvariantCulture)),
            ("Meal Items", summary.MealItemCount.ToString(CultureInfo.InvariantCulture))
        ]);
    }

    private void AddPreparationReport(Section section, DeliveryPreparationSummaryResponse summary)
    {
        AddMetricCards(section,
        [
            ("Orders", summary.OrderCount.ToString(CultureInfo.InvariantCulture)),
            ("Customers", summary.CustomerCount.ToString(CultureInfo.InvariantCulture)),
            ("Meal Items", summary.MealItemCount.ToString(CultureInfo.InvariantCulture))
        ]);

        AddSectionHeading(section, "PREPARATION OVERVIEW");
        AddOverviewTable(section, summary.MealTypes
            .Select(type => (type.MealTypeName, type.Quantity.ToString(CultureInfo.InvariantCulture)))
            .ToArray());

        AddSectionHeading(section, "MENU ITEMS TO PREPARE");
        AddMealTypes(section, summary);

        AddSectionHeading(section, "PLAN BREAKDOWN");
        AddPlanBreakdown(section, summary.PlanBreakdown);
        AddGrandTotal(section, summary);
    }

    private static void AddGrandTotal(Section section, DeliveryPreparationSummaryResponse summary)
    {
        var totalBox = section.AddTable();
        totalBox.KeepTogether = true;
        totalBox.Borders.Width = Unit.FromPoint(0.8);
        totalBox.Borders.Color = Color.Parse("#A9C7BC");
        totalBox.Shading.Color = LightGreen;
        totalBox.Format.SpaceBefore = Unit.FromMillimeter(1.5);
        totalBox.AddColumn(Unit.FromMillimeter(78));
        totalBox.AddColumn(Unit.FromMillimeter(56));
        totalBox.AddColumn(Unit.FromMillimeter(133));
        var row = totalBox.AddRow();
        row.Height = Unit.FromMillimeter(11);
        row.HeightRule = RowHeightRule.AtLeast;
        var label = row.Cells[0].AddParagraph("TOTAL MEAL ITEMS TO PREPARE");
        label.Format.Alignment = ParagraphAlignment.Center;
        label.Format.Font.Bold = true;
        label.Format.Font.Color = Muted;
        label.Format.Font.Size = Unit.FromPoint(7.5);
        var amount = row.Cells[1].AddParagraph(summary.MealItemCount.ToString(CultureInfo.InvariantCulture));
        amount.Format.Alignment = ParagraphAlignment.Center;
        amount.Format.Font.Bold = true;
        amount.Format.Font.Color = Green;
        amount.Format.Font.Size = Unit.FromPoint(25);
        var detail = row.Cells[2].AddParagraph(
            $"Across {summary.OrderCount} {Pluralize(summary.OrderCount, "order", "orders")} and " +
            $"{summary.CustomerCount} {Pluralize(summary.CustomerCount, "customer", "customers")}");
        detail.Format.Font.Color = Muted;
        detail.Format.Font.Size = Unit.FromPoint(7.5);
        SetCellPadding(row, 2);
    }

    private void AddMealTypes(Section section, DeliveryPreparationSummaryResponse summary)
    {
        const int maximumCardRows = 18;
        var pending = new List<DeliveryPreparationMealTypeResponse>(2);

        foreach (var mealType in summary.MealTypes)
        {
            if (mealType.Items.Count > maximumCardRows)
            {
                FlushMealCards(section, summary.Date, pending);
                AddMealTypeTable(section.Elements, summary.Date, mealType, ContentWidthMm);
                continue;
            }

            pending.Add(mealType);
            if (pending.Count == 2)
                FlushMealCards(section, summary.Date, pending);
        }

        FlushMealCards(section, summary.Date, pending);
    }

    private void FlushMealCards(
        Section section,
        DateOnly deliveryDate,
        List<DeliveryPreparationMealTypeResponse> mealTypes)
    {
        if (mealTypes.Count == 0)
            return;

        var layout = section.AddTable();
        layout.KeepTogether = true;
        layout.Borders.Width = 0;
        layout.Format.SpaceAfter = Unit.FromMillimeter(2);
        layout.AddColumn(Unit.FromMillimeter(132));
        layout.AddColumn(Unit.FromMillimeter(3));
        layout.AddColumn(Unit.FromMillimeter(132));
        var row = layout.AddRow();
        AddMealTypeTable(row.Cells[0].Elements, deliveryDate, mealTypes[0], 132);
        if (mealTypes.Count == 2)
            AddMealTypeTable(row.Cells[2].Elements, deliveryDate, mealTypes[1], 132);
        mealTypes.Clear();
    }

    private void AddMealTypeTable(
        DocumentElements container,
        DateOnly deliveryDate,
        DeliveryPreparationMealTypeResponse mealType,
        double widthMm)
    {
        var itemTotal = mealType.Items.Sum(item => item.Quantity);
        if (itemTotal != mealType.Quantity)
        {
            logger.LogWarning(
                "Kitchen preparation source totals are inconsistent. DeliveryDate={DeliveryDate} MealTypeId={MealTypeId} MealType={MealType} SourceQuantity={SourceQuantity} ItemQuantity={ItemQuantity}",
                deliveryDate, mealType.MealTypeId, mealType.MealTypeName,
                mealType.Quantity, itemTotal);
        }

        var table = container.AddTable();
        table.KeepTogether = mealType.Items.Count <= 18;
        table.Format.SpaceAfter = Unit.FromMillimeter(3);
        table.Borders.Color = Border;
        table.Borders.Width = Unit.FromPoint(0.45);
        table.AddColumn(Unit.FromMillimeter(widthMm - 17));
        table.AddColumn(Unit.FromMillimeter(17));

        var title = table.AddRow();
        title.HeadingFormat = true;
        title.Shading.Color = Green;
        title.Cells[0].MergeRight = 1;
        var titleText = title.Cells[0].AddParagraph(mealType.MealTypeName.ToUpperInvariant());
        titleText.Format.Font.Bold = true;
        titleText.Format.Font.Color = Colors.White;
        titleText.Format.Font.Size = Unit.FromPoint(9);
        SetCellPadding(title, 0.9);

        var header = table.AddRow();
        header.HeadingFormat = true;
        header.Format.Font.Bold = true;
        header.Shading.Color = LightGreen;
        header.Format.Font.Size = Unit.FromPoint(8.5);
        header.Cells[0].AddParagraph("Menu item");
        header.Cells[1].AddParagraph("Qty");
        header.Cells[1].Format.Alignment = ParagraphAlignment.Right;
        SetCellPadding(header, 0.8);

        foreach (var item in mealType.Items)
        {
            var row = table.AddRow();
            row.Format.Font.Size = Unit.FromPoint(8.5);
            row.Cells[0].AddParagraph(item.MenuItemName);
            row.Cells[1].AddParagraph(item.Quantity.ToString(CultureInfo.InvariantCulture));
            row.Cells[1].Format.Alignment = ParagraphAlignment.Right;
            SetCellPadding(row, 0.8);
        }

        var total = table.AddRow();
        total.Format.Font.Bold = true;
        total.Format.Font.Size = Unit.FromPoint(8.5);
        total.Shading.Color = LightGray;
        total.Cells[0].AddParagraph("TOTAL");
        total.Cells[1].AddParagraph(mealType.Quantity.ToString(CultureInfo.InvariantCulture));
        total.Cells[1].Format.Alignment = ParagraphAlignment.Right;
        SetCellPadding(total, 0.8);
    }

    private static void AddSectionHeading(Section section, string text)
    {
        var paragraph = section.AddParagraph(text);
        paragraph.Style = "SectionHeading";
        paragraph.Format.KeepWithNext = true;
    }

    private static void AddMetricCards(Section section, IReadOnlyCollection<(string Label, string Value)> metrics)
    {
        var table = section.AddTable();
        table.KeepTogether = true;
        table.Format.LeftIndent = Unit.FromMillimeter(4);
        table.Format.SpaceAfter = Unit.FromMillimeter(3);
        table.Borders.Color = Border;
        table.Borders.Width = Unit.FromPoint(0.5);
        table.Shading.Color = LightGreen;
        var columnWidth = (ContentWidthMm - 8) / metrics.Count;
        foreach (var _ in metrics)
            table.AddColumn(Unit.FromMillimeter(columnWidth));
        var row = table.AddRow();
        var index = 0;
        foreach (var (label, value) in metrics)
        {
            var cell = row.Cells[index++];
            cell.Format.Alignment = ParagraphAlignment.Center;
            var amount = cell.AddParagraph(value);
            amount.Format.Font.Bold = true;
            amount.Format.Font.Size = Unit.FromPoint(18);
            amount.Format.Font.Color = DarkGreen;
            var caption = cell.AddParagraph(label.ToUpperInvariant());
            caption.Format.Font.Bold = true;
            caption.Format.Font.Size = Unit.FromPoint(7.5);
            caption.Format.Font.Color = Muted;
        }
        SetCellPadding(row, 1.4);
    }

    private static void AddOverviewTable(Section section, IReadOnlyCollection<(string Label, string Value)> metrics)
    {
        if (metrics.Count == 0)
            return;

        var table = section.AddTable();
        table.KeepTogether = true;
        table.Format.LeftIndent = Unit.FromMillimeter(4);
        table.Format.SpaceAfter = Unit.FromMillimeter(3);
        table.Borders.Color = Border;
        table.Borders.Width = Unit.FromPoint(0.5);
        var width = (ContentWidthMm - 8) / metrics.Count;
        foreach (var _ in metrics)
            table.AddColumn(Unit.FromMillimeter(width));
        var row = table.AddRow();
        var index = 0;
        foreach (var (label, value) in metrics)
        {
            var cell = row.Cells[index++];
            cell.Format.Alignment = ParagraphAlignment.Center;
            var caption = cell.AddParagraph(label);
            caption.Format.Font.Bold = true;
            caption.Format.Font.Size = Unit.FromPoint(7.5);
            caption.Format.Font.Color = Muted;
            var amount = cell.AddParagraph(value);
            amount.Format.Font.Bold = true;
            amount.Format.Font.Size = Unit.FromPoint(12);
            amount.Format.Font.Color = DarkGreen;
        }
        SetCellPadding(row, 1.2);
    }

    private static void AddPlanBreakdown(
        Section section,
        IReadOnlyCollection<DeliveryPreparationPlanResponse> plans)
    {
        foreach (var group in plans.Chunk(3))
        {
            var table = section.AddTable();
            table.KeepTogether = true;
            table.Format.LeftIndent = Unit.FromMillimeter(18);
            table.Format.SpaceAfter = Unit.FromMillimeter(2);
            table.Borders.Color = Border;
            table.Borders.Width = Unit.FromPoint(0.45);
            var groupWidth = (ContentWidthMm - 36) / group.Length;
            foreach (var _ in group)
            {
                table.AddColumn(Unit.FromMillimeter(groupWidth * 0.68));
                table.AddColumn(Unit.FromMillimeter(groupWidth * 0.32));
            }

            var row = table.AddRow();
            for (var index = 0; index < group.Length; index++)
            {
                var plan = group[index];
                var name = row.Cells[index * 2].AddParagraph(plan.MealPlanName);
                name.Format.Font.Size = Unit.FromPoint(8.5);
                var orders = row.Cells[(index * 2) + 1].AddParagraph(
                    $"{plan.OrderCount} {Pluralize(plan.OrderCount, "order", "orders")}");
                orders.Format.Font.Bold = true;
                orders.Format.Font.Size = Unit.FromPoint(8.5);
            }
            SetCellPadding(row, 1);
        }
    }

    private static void SetCellPadding(Row row, double verticalMm)
    {
        row.TopPadding = Unit.FromMillimeter(verticalMm);
        row.BottomPadding = Unit.FromMillimeter(verticalMm);
        row.VerticalAlignment = VerticalAlignment.Center;
    }

    private static void AddFooter(Section section, DateOnly deliveryDate)
    {
        var footer = section.Footers.Primary.AddTable();
        footer.Borders.Top.Color = Border;
        footer.Borders.Top.Width = Unit.FromPoint(0.5);
        footer.AddColumn(Unit.FromMillimeter(230));
        footer.AddColumn(Unit.FromMillimeter(37));
        var row = footer.AddRow();
        row.Format.Font.Size = Unit.FromPoint(7);
        row.Format.Font.Color = Muted;
        var description = row.Cells[0].AddParagraph("Diet Time - Kitchen Preparation | ");
        description.AddText(deliveryDate.ToString("dd MMM yyyy", CultureInfo.GetCultureInfo("en-GB")));
        var page = row.Cells[1].AddParagraph("Page ");
        page.Format.Alignment = ParagraphAlignment.Right;
        page.AddPageField();
        SetCellPadding(row, 1);
    }

    private static void ConfigureStyles(Document document)
    {
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = FontFamily;
        normal.Font.Size = Unit.FromPoint(9.5);
        normal.Font.Color = DarkGreen;
        normal.ParagraphFormat.SpaceAfter = Unit.Zero;

        AddStyle(document, "Brand", 9, true, Green);
        AddStyle(document, "ReportTitle", 20, true, DarkGreen);
        AddStyle(document, "DeliveryDate", 10.5, true, DarkGreen);
        AddStyle(document, "Generated", 7.5, false, Muted);
        var sectionHeading = AddStyle(document, "SectionHeading", 9, true, Green);
        sectionHeading.ParagraphFormat.SpaceBefore = Unit.FromMillimeter(1.5);
        sectionHeading.ParagraphFormat.SpaceAfter = Unit.FromMillimeter(1);
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
