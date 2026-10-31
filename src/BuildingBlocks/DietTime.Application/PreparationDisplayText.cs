using System.Globalization;

namespace DietTime.Application;

public static class PreparationDisplayText
{
    public static string Label(string code)
    {
        var value = code.Trim();
        var separator = value.IndexOf(':');
        if (separator >= 0) return Label(value[..separator]) + ": " + Label(value[(separator + 1)..]);
        if (!value.Contains('_') && value.Any(char.IsLower)) return value;
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' ').ToLowerInvariant());
    }
}
