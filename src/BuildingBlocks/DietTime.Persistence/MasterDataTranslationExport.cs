using System.Text;
using DietTime.Domain;
using Microsoft.EntityFrameworkCore;

namespace DietTime.Persistence;

public sealed class MasterDataTranslationExport(DietTimeDbContext db)
{
    public static bool Supports(string? resource) => resource is
        "meal-categories" or "ingredients" or "allergens" or "meal-types" or "meals" or "meal-plans";

    public async Task<byte[]> GenerateAsync(string resource, CancellationToken ct, Guid? planId = null)
    {
        if (!Supports(resource)) throw new ArgumentException("Choose a supported master-data screen.", nameof(resource));
        var csv = new StringBuilder();
        Row("English", "Arabic");
        void Row(string? en, string? ar) => csv.AppendLine(Escape(en) + "," + Escape(ar));
        void Translations<T>(IEnumerable<T> translations, params Func<T, string?>[] fields) where T : Translation
        {
            var en = translations.FirstOrDefault(t => t.LanguageCode.Equals("en", StringComparison.OrdinalIgnoreCase));
            var ar = translations.FirstOrDefault(t => t.LanguageCode.Equals("ar", StringComparison.OrdinalIgnoreCase));
            for (var index = 0; index < fields.Length; index++)
            {
                var english = en is null ? null : fields[index](en);
                var arabic = ar is null ? null : fields[index](ar);
                // Keep missing names visible; omit unused optional text fields.
                if (index == 0 || !string.IsNullOrWhiteSpace(english) || !string.IsNullOrWhiteSpace(arabic))
                    Row(english, arabic);
            }
        }

        switch (resource)
        {
            case "meal-categories":
                foreach (var row in await db.MealCategories.AsNoTracking().Include(r => r.Translations).OrderBy(r => r.Code).ToArrayAsync(ct))
                    Translations(row.Translations, t => t.Name, t => t.Description);
                break;
            case "ingredients":
                foreach (var row in await db.Ingredients.AsNoTracking().Include(r => r.Translations).OrderBy(r => r.Code).ToArrayAsync(ct))
                    Translations(row.Translations, t => t.Name, t => t.Description);
                break;
            case "allergens":
                foreach (var row in await db.Allergens.AsNoTracking().Include(r => r.Translations).OrderBy(r => r.Code).ToArrayAsync(ct))
                    Translations(row.Translations, t => t.Name, t => t.Description);
                break;
            case "meal-types":
                foreach (var row in await db.MealTypes.AsNoTracking().Include(r => r.Translations).OrderBy(r => r.Code).ToArrayAsync(ct))
                    Translations(row.Translations, t => t.Name, t => t.Description);
                break;
            case "meals":
                foreach (var row in await db.MealItems.AsNoTracking().Where(r => r.IsLatest).Include(r => r.Translations).OrderBy(r => r.Sku).ToArrayAsync(ct))
                    Translations(row.Translations, t => t.Name, t => t.ShortDescription, t => t.FullDescription,
                        t => t.PreparationInstructions, t => t.ServingNotes);
                break;
            case "meal-plans":
                var plans = db.MealPlanTemplates.AsNoTracking().Where(r => planId.HasValue ? r.Id == planId : r.IsLatest);
                foreach (var row in await plans.Include(r => r.Translations).OrderBy(r => r.Code).ToArrayAsync(ct))
                    Translations(row.Translations, t => t.Name, t => t.ShortDescription, t => t.FullDescription);
                break;
        }
        ct.ThrowIfCancellationRequested();
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
    }

    public static string Escape(string? value)
    {
        var text = value ?? "";
        if (text.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' || text.StartsWith('\t') || text.StartsWith('\r')) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
