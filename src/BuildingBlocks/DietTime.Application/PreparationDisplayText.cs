using System.Globalization;

namespace DietTime.Application;

public static class PreparationDisplayText
{
    // Only an anchored legacy discriminator is removed; descriptive text is retained.
    public static string AllergenLabel(string value) => Label(
        value.Trim().StartsWith("Allergen:", StringComparison.OrdinalIgnoreCase)
            ? value.Trim()["Allergen:".Length..].Trim() : value);

    public static string AllergenLabel(string value, string? notes) =>
        AllergenLabel(value) + (string.IsNullOrWhiteSpace(notes) ? "" : " (" + notes.Trim() + ")");

    public static (IReadOnlyList<string> Preferences, IReadOnlyList<string> Allergens) Dietary(
        IReadOnlyList<string>? preferences, IReadOnlyList<string>? allergens, IEnumerable<string> legacy)
    {
        var values = legacy.ToArray();
        return (Clean(preferences ?? values.Where(v => !v.TrimStart().StartsWith("Allergen:", StringComparison.OrdinalIgnoreCase)).ToArray()),
            Clean((allergens ?? values.Where(v => v.TrimStart().StartsWith("Allergen:", StringComparison.OrdinalIgnoreCase)).ToArray()).Select(AllergenLabel)));
    }

    public static IReadOnlyList<string> Clean(IEnumerable<string> values) => values
        .Where(v => !string.IsNullOrWhiteSpace(v) && !v.Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase))
        .Select(v => Label(v.Trim())).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string Label(string code)
    {
        var value = code.Trim();
        var separator = value.IndexOf(':');
        if (separator >= 0) return Label(value[..separator]) + ": " + Label(value[(separator + 1)..]);
        if (!value.Contains('_') && value.Any(char.IsLower)) return value;
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' ').ToLowerInvariant());
    }
}
