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

public static class MealStatuses
{
    public static bool IsValid(string? status) => status?.Trim().ToUpperInvariant() is
        "DRAFT" or "ACTIVE" or "INACTIVE" or "ARCHIVED";

    public static string Normalize(string status) => status.Trim().ToUpperInvariant();
}

public static class SelectionRules
{
    public static bool IsCountValid(int count, int minimum, int maximum) => count >= minimum && count <= maximum;
    public static decimal ResolveAdditionalPrice(decimal slotPrice, bool allowsPaidUpgrade) => allowsPaidUpgrade ? slotPrice : 0;
}

public static class MediaObjectKeyRules
{
    public static bool IsAllowed(string? objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            return false;

        var segments = objectKey.Split('/');
        if (segments.Length != 4 ||
            segments.Any(segment => segment.Length == 0 || segment is "." or "..") ||
            !Guid.TryParse(segments[1], out _))
        {
            return false;
        }

        return segments[0] switch
        {
            "meals" => segments[2] is "images" or "thumbnails",
            "meal-plans" => segments[2] == "images",
            _ => false
        };
    }
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
