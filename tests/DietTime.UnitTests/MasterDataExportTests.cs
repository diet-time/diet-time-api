using DietTime.Persistence;

namespace DietTime.UnitTests;

public sealed class MasterDataExportTests
{
    [Theory]
    [InlineData("Plain Arabic عربي", "\"Plain Arabic عربي\"")]
    [InlineData("Contains, comma", "\"Contains, comma\"")]
    [InlineData("Quote \"text\"", "\"Quote \"\"text\"\"\"")]
    [InlineData("Line one\nLine two", "\"Line one\nLine two\"")]
    [InlineData("=SUM(A1)", "\"'=SUM(A1)\"")]
    [InlineData("  +123", "\"'  +123\"")]
    [InlineData("@command", "\"'@command\"")]
    public void Csv_fields_preserve_bilingual_text_and_quoting_without_executing_formulas(string input, string expected) =>
        Assert.Equal(expected, MasterDataTranslationExport.Escape(input));
}
