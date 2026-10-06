using RecordFlow.Core.Workspaces;

namespace RecordFlow.Core.Services;

public sealed record PhoneLink(string Label, string Number, string TelUri);

/// <summary>Click-to-call helpers: turns valid U.S. phone numbers from the CSV into tel: links.</summary>
public static class PhoneNumbers
{
    /// <summary>"tel:+12175550142" (with ";ext=12" when an extension is given), or null when the value isn't a U.S. phone number.</summary>
    public static string? ToTelUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !FieldValidator.IsUsPhone(value.Trim())) return null;
        value = value.Trim();

        var extAt = value.IndexOf("ext", StringComparison.OrdinalIgnoreCase);
        if (extAt < 0) extAt = value.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        var main = extAt > 0 ? value[..extAt] : value;
        var ext = extAt > 0 ? new string(value[extAt..].Where(char.IsDigit).ToArray()) : "";

        var digits = new string(main.Where(char.IsDigit).ToArray());
        if (digits.Length == 11) digits = digits[1..];
        return $"tel:+1{digits}" + (ext.Length > 0 ? $";ext={ext}" : "");
    }

    /// <summary>Every dialable phone number in the record's CSV row, in column order.</summary>
    public static IReadOnlyList<PhoneLink> FromRecord(WorkingRecord record, IReadOnlyList<string> headers) =>
        headers
            .Select(h => (Header: h, Value: record.GetCsvValue(h)))
            .Select(x => (x.Header, x.Value, Tel: ToTelUri(x.Value)))
            .Where(x => x.Tel is not null)
            .Select(x => new PhoneLink(x.Header, x.Value!.Trim(), x.Tel!))
            .ToList();

    /// <summary>The number a caller should dial first: the store phone when there is one, otherwise the first phone column.</summary>
    public static PhoneLink? Primary(WorkingRecord record, IReadOnlyList<string> headers)
    {
        var phones = FromRecord(record, headers);
        return phones.FirstOrDefault(p => HeaderNormalizer.Normalize(p.Label).StartsWith("storephone", StringComparison.Ordinal))
            ?? phones.FirstOrDefault(p => HeaderNormalizer.Normalize(p.Label).Contains("phone", StringComparison.Ordinal))
            ?? phones.FirstOrDefault();
    }
}
