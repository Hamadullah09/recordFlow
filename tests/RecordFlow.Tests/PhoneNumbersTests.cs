using RecordFlow.Core.Services;
using RecordFlow.Core.Workspaces;

namespace RecordFlow.Tests;

public class PhoneNumbersTests
{
    [Theory]
    [InlineData("(217) 555-0142", "tel:+12175550142")]
    [InlineData("602-555-0123", "tel:+16025550123")]
    [InlineData("+1 504.555.0101", "tel:+15045550101")]
    [InlineData("(800) 555-0110 ext 12", "tel:+18005550110;ext=12")]
    public void Valid_us_numbers_become_tel_links(string value, string expected) =>
        Assert.Equal(expected, PhoneNumbers.ToTelUri(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Station 7")]
    [InlineData("ALM-55821")]
    [InlineData("555-0142")]
    [InlineData("=HYPERLINK(\"tel:123\")")]
    public void Anything_else_is_not_dialable(string? value) =>
        Assert.Null(PhoneNumbers.ToTelUri(value));

    [Fact]
    public void Store_phone_is_dialed_first()
    {
        var headers = new[] { "Present Phone Number", "Store Name", "Store Phone Number", "Alarm Service ID" };
        var record = new WorkingRecord
        {
            CsvValues = new()
            {
                ["Present Phone Number"] = "(217) 555-0199",
                ["Store Name"] = "Main Street Market",
                ["Store Phone Number"] = "(217) 555-0142",
                ["Alarm Service ID"] = "ALM-55821",
            },
        };

        Assert.Equal(2, PhoneNumbers.FromRecord(record, headers).Count);
        Assert.Equal("tel:+12175550142", PhoneNumbers.Primary(record, headers)!.TelUri);

        record.CsvValues.Remove("Store Phone Number");
        Assert.Equal("tel:+12175550199", PhoneNumbers.Primary(record, headers)!.TelUri);
    }
}
