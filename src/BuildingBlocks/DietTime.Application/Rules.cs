namespace DietTime.Application;

public sealed record LocalizedValue(string LanguageCode, string Value);

public static class LocalizationFallback
{
    public static string? Resolve(IEnumerable<LocalizedValue> values, string requestedLanguage)
    {
        var items = values.ToArray();
        return items.FirstOrDefault(x => x.LanguageCode.Equals(requestedLanguage, StringComparison.OrdinalIgnoreCase))?.Value
            ?? items.FirstOrDefault(x => x.LanguageCode.Equals("en", StringComparison.OrdinalIgnoreCase))?.Value
            ?? items.FirstOrDefault()?.Value;
    }
}

public static class MealAvailability
{
    public static bool IsAvailable(string status, bool isAvailable, DateTimeOffset? from, DateTimeOffset? until, DateTimeOffset now) =>
        status == "ACTIVE" && isAvailable && (from is null || from <= now) && (until is null || until > now);
}

public static class TemplateCalendar
{
    public static int ResolveDayNumber(DateOnly startDate, DateOnly date, int durationDays)
    {
        if (durationDays <= 0) throw new ArgumentOutOfRangeException(nameof(durationDays));
        var offset = date.DayNumber - startDate.DayNumber;
        return ((offset % durationDays) + durationDays) % durationDays + 1;
    }

    public static short IsoDayOfWeek(DateOnly date) => (short)(date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek);
}

public static class SelectionRules
{
    public static bool IsCountValid(int count, int minimum, int maximum) => count >= minimum && count <= maximum;
    public static decimal ResolveAdditionalPrice(decimal slotPrice, decimal variantPrice, bool allowsPaidUpgrade) => allowsPaidUpgrade ? slotPrice + variantPrice : 0;
}

public sealed class LanguageResolver : ILanguageResolver
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase) { "en", "ar" };

    public string Resolve(string? queryLanguage, string? acceptLanguage)
    {
        var candidate = !string.IsNullOrWhiteSpace(queryLanguage) ? queryLanguage : acceptLanguage?.Split(',', ';')[0];
        candidate = candidate?.Trim().ToLowerInvariant() ?? "en";
        if (!Supported.Contains(candidate)) throw new UnsupportedLanguageException(candidate);
        return candidate;
    }
}

public sealed class UnsupportedLanguageException(string language) : Exception($"Unsupported language '{language}'. Supported values are 'en' and 'ar'.");

