using System.Globalization;
using DietTime.Application;
using DietTime.Contracts;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;

namespace DietTime.Infrastructure;

public sealed partial class KitchenPreparationPdfReportGenerator
{
    private static void AddReportStatus(Section section, DeliveryPreparationSummaryResponse summary)
    {
        if (summary.MealItemCount == 0 && summary.Selections.Count == 0 && !summary.IsPreliminary) return;
        var note = section.AddParagraph(summary.IsPreliminary
            ? "PRELIMINARY - confirmed or automatically assigned locked selections only. Review outstanding orders before preparing."
            : "Preparation quantities based on confirmed meal selections.");
        note.Format.Font.Size = Unit.FromPoint(8);
        note.Format.Font.Color = summary.IsPreliminary ? Color.Parse("#9A5A18") : Muted;
        note.Format.Shading.Color = summary.IsPreliminary ? Color.Parse("#FFF4E5") : LightGray;
        note.Format.LeftIndent = Unit.FromMillimeter(2);
        note.Format.SpaceBefore = Unit.FromMillimeter(1);
        note.Format.SpaceAfter = Unit.FromMillimeter(3);
        note.Format.KeepWithNext = true;
    }

    private static void AddReviewNotes(Section section, DeliveryPreparationSummaryResponse summary)
    {
        if (summary.OperationalExceptions.Count == 0) return;
        AddSectionHeading(section, "ORDERS NEEDING REVIEW");
        foreach (var warning in summary.OperationalExceptions)
        {
            var paragraph = section.AddParagraph("Review required: " + warning);
            paragraph.Format.Font.Size = Unit.FromPoint(8);
            paragraph.Format.Font.Color = Color.Parse("#9A3E28");
            paragraph.Format.Shading.Color = Color.Parse("#FFF2ED");
            paragraph.Format.LeftIndent = Unit.FromMillimeter(2);
            paragraph.Format.SpaceAfter = Unit.FromMillimeter(2);
        }
    }

    private void AddCustomerOrders(Document document, Section overview, DeliveryPreparationSummaryResponse summary)
    {
        if (summary.Selections.Count == 0) return;
        // Packing information starts on a new page, leaving the kitchen overview uncluttered.
        var section = document.AddSection();
        section.PageSetup = overview.PageSetup.Clone();
        section.PageSetup.SectionStart = BreakType.BreakNextPage;
        AddBrandHeader(section, summary.Date);
        AddFooter(section, summary.Date);
        var title = section.AddParagraph("Customer Orders");
        title.Style = "ReportTitle";
        var date = section.AddParagraph(summary.Date.ToString("dddd, d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB")));
        date.Style = "DeliveryDate";
        var caption = section.AddParagraph("Packing list - check the order number, delivery method and allergens for every order.");
        caption.Style = "Generated";
        caption.Format.SpaceAfter = Unit.FromMillimeter(4);

        var customerIndex = 0;
        foreach (var customer in summary.Selections.GroupBy(s => s.CustomerReference)
                     .OrderBy(g => g.First().CustomerName, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key))
        {
            var orders = customer.OrderBy(s => s.OrderReference, StringComparer.OrdinalIgnoreCase).ToArray();
            var background = customerIndex++ % 2 == 0 ? Colors.White : LightGray;
            var compact = orders.Length <= 2 && orders.Sum(s => s.Dishes.Count) <= 5 &&
                orders.Sum(s => (s.DeliveryAddress?.Length ?? 0) + (s.TakeawayBranchAddress?.Length ?? 0) +
                    (s.ReviewReason?.Length ?? 0) + (s.CustomerName?.Length ?? 0) +
                    s.Dishes.Sum(d => d.MealName.Length + (d.MealCategoryName?.Length ?? 0)) +
                    (s.Allergens?.Sum(a => a.Length) ?? 0) + (s.FoodPreferences?.Sum(p => p.Length) ?? 0) +
                    s.DietaryRestrictions.Sum(d => d.Length) + s.DietaryRestrictionLabels.Sum(d => d.Length)) < 1400;
            // Keep a small customer's entire group together; large groups can flow safely.
            DocumentElements container = section.Elements;
            if (compact)
            {
                var group = section.AddTable();
                group.KeepTogether = true;
                group.Rows.LeftIndent = Unit.Zero;
                group.LeftPadding = Unit.Zero;
                group.RightPadding = Unit.Zero;
                group.AddColumn(Unit.FromMillimeter(ContentWidthMm));
                container = group.AddRow().Cells[0].Elements;
            }
            foreach (var order in orders)
            {
                AddCustomerOrderTable(container, order, background, compact);
                var gap = container.AddParagraph();
                gap.Format.Font.Size = Unit.FromPoint(1);
                gap.Format.SpaceAfter = Unit.FromMillimeter(3);
            }
        }
    }

    private static void AddCustomerOrderTable(DocumentElements container, KitchenSelectionResponse selection, Color background, bool compact)
    {
        var table = container.AddTable();
        table.KeepTogether = compact || (selection.Dishes.Count <= 10 &&
            (selection.DeliveryAddress?.Length ?? 0) + (selection.TakeawayBranchAddress?.Length ?? 0) +
            selection.Dishes.Sum(d => d.MealName.Length) + (selection.Allergens?.Sum(a => a.Length) ?? 0) +
            selection.DietaryRestrictions.Sum(d => d.Length) + selection.DietaryRestrictionLabels.Sum(d => d.Length) < 1000);
        table.Rows.LeftIndent = Unit.Zero;
        table.Borders.Width = Unit.FromPoint(0.6);
        table.Borders.Color = Border;
        table.Shading.Color = background;
        table.LeftPadding = Unit.FromMillimeter(3);
        table.RightPadding = Unit.FromMillimeter(3);
        table.Format.Font.Size = Unit.FromPoint(8.5);
        table.Format.SpaceAfter = Unit.Zero;
        table.AddColumn(Unit.FromMillimeter(ContentWidthMm - 22));
        table.AddColumn(Unit.FromMillimeter(22));

        Row Band(Color color, bool heading = true)
        {
            var row = table.AddRow();
            row.HeadingFormat = heading;
            row.Cells[0].MergeRight = 1;
            row.Shading.Color = color;
            SetCellPadding(row, 1.5);
            return row;
        }
        var heading = Band(Green).Cells[0];
        var name = heading.AddParagraph(selection.CustomerName ?? "Name not recorded");
        name.Format.Font.Color = Colors.White;
        name.Format.Font.Bold = true;
        name.Format.Font.Size = Unit.FromPoint(13);
        var reference = heading.AddParagraph("Order " + selection.OrderReference);
        reference.Format.Font.Color = Colors.White;
        reference.Format.Font.Size = Unit.FromPoint(8.5);
        if (!string.IsNullOrWhiteSpace(selection.MealPlanName))
        {
            var plan = heading.AddParagraph(selection.MealPlanName);
            plan.Format.Font.Color = Colors.White;
            plan.Format.Font.Size = Unit.FromPoint(8);
        }

        var takeaway = selection.FulfillmentMethod == "TAKEAWAY";
        var location = Band(LightGreen).Cells[0];
        var label = location.AddParagraph(takeaway ? "COLLECTION LOCATION · Takeaway" : "DELIVERY LOCATION · Home Delivery");
        label.Format.Font.Bold = true;
        label.Format.Font.Color = Green;
        if (takeaway)
        {
            location.AddParagraph(string.IsNullOrWhiteSpace(selection.TakeawayBranchName) ? "Branch verification required" : selection.TakeawayBranchName);
            location.AddParagraph(PreparationOrderLocation.IsGeneric(selection.TakeawayBranchAddress)
                ? "Address verification required" : selection.TakeawayBranchAddress!);
        }
        else
        {
            var missingAddress = PreparationOrderLocation.IsGeneric(selection.DeliveryAddress);
            if (selection.DeliveryAddressVerificationRequired || missingAddress)
            {
                var warning = location.AddParagraph("Address verification required");
                warning.Format.Font.Bold = true;
                warning.Format.Font.Color = Color.Parse("#B42318");
            }
            if (!missingAddress) location.AddParagraph(selection.DeliveryAddress!);
            if (selection.DeliveryLatitude is { } latitude && selection.DeliveryLongitude is { } longitude &&
                latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180)
            {
                var coordinates = FormattableString.Invariant($"{latitude:0.######}, {longitude:0.######}");
                var map = location.AddParagraph("Coordinates: " + coordinates + "  |  ");
                var link = map.AddHyperlink("https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(coordinates), HyperlinkType.Web);
                link.AddText("Open map");
                link.Font.Color = Green;
                link.Font.Underline = Underline.Single;
            }
        }
        var legacy = selection.DietaryRestrictionLabels.Count > 0 ? selection.DietaryRestrictionLabels : selection.DietaryRestrictions;
        var dietary = PreparationDisplayText.Dietary(selection.FoodPreferences, selection.Allergens, legacy);
        Band(background).Cells[0].AddParagraph("Food Preferences: " + (dietary.Preferences.Count > 0 ? string.Join(", ", dietary.Preferences) : "None specified"));
        var allergens = Band(dietary.Allergens.Count > 0 ? Color.Parse("#FFF0ED") : background).Cells[0]
            .AddParagraph("Allergens: " + (dietary.Allergens.Count > 0 ? string.Join(", ", dietary.Allergens) : "None specified"));
        allergens.Format.Font.Bold = true;
        allergens.Format.Font.Color = dietary.Allergens.Count > 0 ? Color.Parse("#B42318") : Muted;
        if (!string.IsNullOrWhiteSpace(selection.ReviewReason))
        {
            var review = Band(Color.Parse("#FFF4E5")).Cells[0].AddParagraph("Kitchen review: " + selection.ReviewReason);
            review.Format.Font.Color = Color.Parse("#9A3E28");
        }
        var columns = table.AddRow();
        columns.HeadingFormat = true;
        columns.Shading.Color = LightGreen;
        columns.Format.Font.Bold = true;
        columns.Cells[0].AddParagraph("MEALS FOR THIS ORDER");
        columns.Cells[1].AddParagraph("Qty");
        columns.Cells[1].Format.Alignment = ParagraphAlignment.Right;
        SetCellPadding(columns, 1.2);
        foreach (var category in selection.Dishes.GroupBy(d => new
                 {
                     Name = d.MealCategoryName ?? (d.MealClassification == "snack" ? "Uncategorized Snack" : "Uncategorized Meal"),
                     d.MealCategoryDisplayOrder
                 }).OrderBy(g => g.Key.MealCategoryDisplayOrder).ThenBy(g => g.Key.Name))
        {
            var categoryRow = Band(LightGray, false);
            categoryRow.KeepWith = 1;
            var categoryName = categoryRow.Cells[0].AddParagraph(category.Key.Name);
            categoryName.Format.Font.Bold = true;
            categoryName.Format.Font.Color = Green;
            SetCellPadding(categoryRow, 0.8);
            foreach (var dish in category)
            {
                var row = table.AddRow();
                row.Cells[0].AddParagraph(dish.MealName);
                row.Cells[1].AddParagraph(dish.Quantity.ToString(CultureInfo.InvariantCulture));
                row.Cells[1].Format.Alignment = ParagraphAlignment.Right;
                SetCellPadding(row, 0.8);
            }
        }
        if (selection.Dishes.Count == 0)
            Band(background, false).Cells[0].AddParagraph("No confirmed meal selections yet.");
        var total = table.AddRow();
        total.Shading.Color = LightGreen;
        total.Format.Font.Bold = true;
        total.Cells[0].AddParagraph("MEAL ITEMS IN THIS ORDER");
        total.Cells[1].AddParagraph(selection.Dishes.Sum(d => d.Quantity).ToString(CultureInfo.InvariantCulture));
        total.Cells[1].Format.Alignment = ParagraphAlignment.Right;
        SetCellPadding(total, 1.5);
    }
}
