using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;

namespace DietTime.Infrastructure;

public sealed partial class KitchenPreparationPdfReportGenerator
{
    private static readonly Lazy<string> ReportLogo = new(() =>
    {
        using var stream = typeof(KitchenPreparationPdfReportGenerator).Assembly
            .GetManifestResourceStream("DietTime.Reports.Logo.png")
            ?? throw new InvalidOperationException("The embedded Diet Time report logo is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return "base64:" + Convert.ToBase64String(bytes.ToArray());
    });

    private void AddBrandHeader(Section section, DateOnly deliveryDate)
    {
        var header = section.Headers.Primary.AddTable();
        header.Rows.LeftIndent = Unit.Zero;
        header.LeftPadding = Unit.Zero;
        header.RightPadding = Unit.Zero;
        header.Format.SpaceAfter = Unit.Zero;
        header.Borders.Bottom.Color = Border;
        header.Borders.Bottom.Width = Unit.FromPoint(0.8);
        header.AddColumn(Unit.FromMillimeter(24));
        header.AddColumn(Unit.FromMillimeter(159));
        header.AddColumn(Unit.FromMillimeter(84));
        var row = header.AddRow();
        row.VerticalAlignment = VerticalAlignment.Center;
        var logo = row.Cells[0].AddImage(ReportLogo.Value);
        logo.LockAspectRatio = true;
        logo.Width = Unit.FromMillimeter(22);
        var brand = row.Cells[1].AddParagraph("DIET TIME");
        brand.Format.Font.Size = Unit.FromPoint(13);
        brand.Format.Font.Bold = true;
        brand.Format.Font.Color = Green;
        var reportName = row.Cells[1].AddParagraph("Kitchen Preparation");
        reportName.Format.Font.Size = Unit.FromPoint(9);
        reportName.Format.Font.Color = Muted;
        var date = row.Cells[2].AddParagraph(deliveryDate.ToString("dddd, d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB")));
        date.Format.Alignment = ParagraphAlignment.Right;
        date.Format.Font.Size = Unit.FromPoint(8);
        date.Format.Font.Color = Muted;
        var generatedAt = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), businessTimeZone);
        var generated = row.Cells[2].AddParagraph("Generated " + generatedAt.ToString("dd MMM yyyy, hh:mm tt", CultureInfo.GetCultureInfo("en-US")));
        generated.Format.Alignment = ParagraphAlignment.Right;
        generated.Format.Font.Size = Unit.FromPoint(7);
        generated.Format.Font.Color = Muted;
    }
}
