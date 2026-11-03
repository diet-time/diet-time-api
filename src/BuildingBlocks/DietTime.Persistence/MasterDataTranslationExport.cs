using System.Text;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed class MasterDataTranslationExport(DietTimeDbContext db)
{
    public async Task<byte[]> GenerateAsync(CancellationToken ct)
    {
        var csv = new StringBuilder();
        Row("Master data", "Record ID", "Code / reference", "Version", "Status", "Field", "English", "Arabic", "Translation review");
        void Row(params string?[] cells) => csv.AppendLine(string.Join(",", cells.Select(Escape)));
        void Field(string type, string id, string code, string version, string status, string field, string? en, string? ar, bool shared = false)
        {
            var review = shared ? "Shared field (not separately localized)" :
                string.IsNullOrWhiteSpace(en) && string.IsNullOrWhiteSpace(ar) ? "English and Arabic missing" :
                string.IsNullOrWhiteSpace(en) ? "English missing" : string.IsNullOrWhiteSpace(ar) ? "Arabic missing" : "Both provided";
            Row(type, id, code, version, status, field, en, ar, review);
        }
        void Translations<T>(string type, Guid id, string code, string status, IEnumerable<T> translations,
            string version, params (string Name, Func<T, string?> Text)[] fields) where T : Translation
        {
            var en = translations.FirstOrDefault(t => t.LanguageCode.Equals("en", StringComparison.OrdinalIgnoreCase));
            var ar = translations.FirstOrDefault(t => t.LanguageCode.Equals("ar", StringComparison.OrdinalIgnoreCase));
            foreach (var field in fields)
                Field(type, id.ToString(), code, version, status, field.Name, en is null ? null : field.Text(en), ar is null ? null : field.Text(ar));
        }
        static string Active(bool active) => active ? "Active" : "Inactive";
        foreach (var r in await db.MealCategories.AsNoTracking().Include(r => r.Translations).OrderBy(r => r.Code).ToArrayAsync(ct))
            Translations("Meal categories", r.Id, r.Code, Active(r.IsActive), r.Translations, "", ("Name", t => t.Name), ("Description", t => t.Description));
        foreach (var r in await db.Ingredients.AsNoTracking().Include(r => r.Translations).OrderBy(r => r.Code).ToArrayAsync(ct))
            Translations("Ingredients", r.Id, r.Code, Active(r.IsActive), r.Translations, "", ("Name", t => t.Name), ("Description", t => t.Description));
        foreach (var r in await db.Allergens.AsNoTracking().Include(r => r.Translations).OrderBy(r => r.Code).ToArrayAsync(ct))
            Translations("Allergens", r.Id, r.Code, Active(r.IsActive), r.Translations, "", ("Name", t => t.Name), ("Description", t => t.Description));
        foreach (var r in await db.MealTypes.AsNoTracking().Include(r => r.Translations).OrderBy(r => r.Code).ToArrayAsync(ct))
            Translations("Meal types", r.Id, r.Code, Active(r.IsActive), r.Translations, "", ("Name", t => t.Name), ("Description", t => t.Description));
        foreach (var r in await db.MealItems.AsNoTracking().Include(r => r.Translations).OrderBy(r => r.Sku).ThenBy(r => r.VersionNumber).ToArrayAsync(ct))
            Translations("Meals", r.Id, r.Sku, r.Status, r.Translations, $"{r.VersionNumber} ({(r.IsLatest ? "latest" : "history")})",
                ("Name", t => t.Name), ("Short description", t => t.ShortDescription), ("Full description", t => t.FullDescription),
                ("Preparation instructions", t => t.PreparationInstructions), ("Serving notes", t => t.ServingNotes));
        foreach (var r in await db.MealPlanTemplates.AsNoTracking().Include(r => r.Translations).OrderBy(r => r.Code).ThenBy(r => r.VersionNumber).ToArrayAsync(ct))
            Translations("Meal plans", r.Id, r.Code, Active(r.IsActive), r.Translations, $"{r.VersionNumber} ({(r.IsLatest ? "latest" : "history")})",
                ("Name", t => t.Name), ("Short description", t => t.ShortDescription), ("Full description", t => t.FullDescription));
        foreach (var r in await db.MealPlanTemplateSlots.AsNoTracking().Include(r => r.Translations).OrderBy(r => r.Id).ToArrayAsync(ct))
            Translations("Plan slots", r.Id, r.MealPlanTemplateDayId.ToString(), Active(r.IsActive), r.Translations, "",
                ("Title", t => t.Title), ("Instruction", t => t.Instruction));
        foreach (var r in await db.MealPlanPrices.AsNoTracking().Include(r => r.Translations).OrderBy(r => r.Id).ToArrayAsync(ct))
            Translations("Plan price labels", r.Id, r.PackageCode ?? r.MealPlanTemplateId.ToString(), Active(r.IsActive), r.Translations, "",
                ("Name", t => t.Name), ("Description", t => t.Description));
        foreach (var r in await db.MealPlanPricePackages.AsNoTracking().OrderBy(r => r.DisplayOrder).ThenBy(r => r.Code).ToArrayAsync(ct))
            Field("Durations", r.Code, r.Code, "", Active(r.IsActive), "Name", r.NameEn, r.NameAr);
        foreach (var r in await db.MealPackageOptions.AsNoTracking().OrderBy(r => r.DisplayOrder).ThenBy(r => r.Id).ToArrayAsync(ct))
            Field("Package options", r.Id.ToString(), $"{r.MealCount} meals / {r.SnackCount} snacks", "", Active(r.IsActive), "Name", r.Name, null, true);
        foreach (var r in await db.DeliveryTimeSlots.AsNoTracking().OrderBy(r => r.Id).ToArrayAsync(ct))
            Field("Delivery time slots", r.Id.ToString(), r.Id.ToString(), "", Active(r.IsActive), "Name", r.Name, r.NameAr);
        foreach (var r in await db.TakeawayBranches.AsNoTracking().OrderBy(r => r.Name).ToArrayAsync(ct))
        {
            Field("Takeaway branches", r.Id.ToString(), "", "", Active(r.IsActive), "Name", r.Name, null, true);
            Field("Takeaway branches", r.Id.ToString(), "", "", Active(r.IsActive), "Address", r.Address, null, true);
        }
        foreach (var r in await db.MealMedia.AsNoTracking().Include(r => r.Translations).OrderBy(r => r.Id).ToArrayAsync(ct))
            Translations("Media text", r.Id, r.EntityId.ToString(), r.Status, r.Translations, "", ("Alt text", t => t.AltText), ("Caption", t => t.Caption));
        ct.ThrowIfCancellationRequested();
        // BOM keeps Arabic readable when the CSV is opened directly in Excel.
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
    }

    public static string Escape(string? value)
    {
        var text = value ?? "";
        if (text.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' || text.StartsWith('\t') || text.StartsWith('\r')) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
