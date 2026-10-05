using RecordFlow.Core;
using RecordFlow.Core.Security;
using RecordFlow.Core.Services;

namespace RecordFlow.Tests;

public class SecurityAndValidationTests
{
    [Theory]
    [InlineData("=SUM(A1:A2)", "'=SUM(A1:A2)")]
    [InlineData("+1 555", "'+1 555")]
    [InlineData("-10", "'-10")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("\t=1", "'\t=1")]
    [InlineData("Main Street", "Main Street")]
    [InlineData("", "")]
    public void Spreadsheet_export_neutralizes_formula_injection(string input, string expected) =>
        Assert.Equal(expected, CsvSanitizer.EscapeForSpreadsheet(input));

    [Fact]
    public void Tokens_are_url_safe_and_unique()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => SecureTokens.NewToken()).ToList();
        Assert.Equal(200, tokens.Distinct().Count());
        Assert.All(tokens, t => Assert.True(SecureTokens.LooksLikeToken(t, 30, 64)));
        Assert.False(SecureTokens.LooksLikeToken("../../etc/passwd"));
    }

    [Fact]
    public void Order_and_confirmation_numbers_have_readable_formats()
    {
        Assert.Matches(@"^RF-\d{6}-[0-9A-Z]{6}$", SecureTokens.NewOrderNumber(DateTime.UtcNow));
        Assert.Matches(@"^CNF-[0-9A-Z]{4}-[0-9A-Z]{4}$", SecureTokens.NewConfirmationNumber());
    }

    [Theory]
    [InlineData("(217) 555-0142", true)]
    [InlineData("217-555-0142", true)]
    [InlineData("+1 217 555 0142", true)]
    [InlineData("217.555.0142 ext 12", true)]
    [InlineData("12345", false)]
    [InlineData("055-555-0142", false)]
    [InlineData("call me", false)]
    public void Validates_us_phone_numbers(string value, bool valid) =>
        Assert.Equal(valid, FieldValidator.IsUsPhone(value));

    [Theory]
    [InlineData(FieldType.ZipCode, "62701", null)]
    [InlineData(FieldType.ZipCode, "62701-1234", null)]
    [InlineData(FieldType.ZipCode, "6270", "ZIP")]
    [InlineData(FieldType.Email, "owner@store.com", null)]
    [InlineData(FieldType.Email, "owner@", "email")]
    [InlineData(FieldType.State, "Illinois", null)]
    [InlineData(FieldType.State, "IL", null)]
    [InlineData(FieldType.State, "Ontario", "state")]
    public void Validates_field_types(FieldType type, string value, string? errorFragment)
    {
        var error = FieldValidator.Validate("Field", type, false, 250, [], value);
        if (errorFragment is null) Assert.Null(error);
        else Assert.Contains(errorFragment, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Required_fields_must_have_a_value()
    {
        Assert.NotNull(FieldValidator.Validate("Owner Name", FieldType.Text, true, 100, [], "  "));
        Assert.Null(FieldValidator.Validate("Owner Name", FieldType.Text, false, 100, [], null));
    }

    [Fact]
    public void Pricing_rounds_tax_to_the_cent()
    {
        var quote = PricingCalculator.Quote(new PricingSettings { UnitPrice = 49.99m, TaxRatePercent = 6.25m, ProcessingFee = 1.5m });
        Assert.Equal(49.99m, quote.Subtotal);
        Assert.Equal(3.12m, quote.Tax);
        Assert.Equal(54.61m, quote.Total);
        Assert.Equal(5461, PricingCalculator.ToMinorUnits(quote.Total));
    }

    [Fact]
    public void State_names_normalize_to_codes() =>
        Assert.Equal("NY", UsStates.Normalize(" new york "));
}
