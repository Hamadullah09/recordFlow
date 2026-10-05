using System.Globalization;
using System.Net.Mail;
using System.Text.RegularExpressions;
using RecordFlow.Core.Workspaces;

namespace RecordFlow.Core.Services;

/// <summary>Server-side validation for dynamically generated form fields.</summary>
public static partial class FieldValidator
{
    [GeneratedRegex(@"^\d{5}(-\d{4})?$")]
    private static partial Regex ZipRegex();

    public static string? Validate(WorkingField field, string? value) =>
        Validate(field.Label, field.FieldType, field.IsRequired, field.MaxLength, field.Options, value);

    public static string? Validate(string label, FieldType type, bool required, int maxLength, IReadOnlyList<string> options, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return required ? $"{label} is required." : null;

        value = value.Trim();
        if (maxLength > 0 && value.Length > maxLength)
            return $"{label} must be {maxLength} characters or fewer.";

        return type switch
        {
            FieldType.Phone when !IsUsPhone(value) => $"{label} must be a valid U.S. phone number, e.g. (555) 123-4567.",
            FieldType.Email when !IsEmail(value) => $"{label} must be a valid email address.",
            FieldType.ZipCode when !ZipRegex().IsMatch(value) => $"{label} must be a 5-digit ZIP code (or ZIP+4).",
            FieldType.State when !UsStates.IsValid(value) => $"{label} must be a U.S. state.",
            FieldType.Number when !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _) => $"{label} must be a number.",
            FieldType.Date when !DateTime.TryParse(value, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out _) => $"{label} must be a valid date (MM/DD/YYYY).",
            FieldType.YesNo when value is not ("Y" or "N") => $"{label} must be Yes or No.",
            FieldType.Select when options.Count > 0 && !options.Contains(value, StringComparer.OrdinalIgnoreCase) => $"{label} must be one of the listed options.",
            _ => null,
        };
    }

    /// <summary>Normalizes a value before saving (trims, upper-cases state codes).</summary>
    public static string? Normalize(FieldType type, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return type switch
        {
            FieldType.State => UsStates.Normalize(value) ?? value,
            FieldType.Email => value.ToLowerInvariant(),
            FieldType.YesNo => value.ToUpperInvariant(),
            _ => value,
        };
    }

    public static bool IsUsPhone(string value)
    {
        var mainPart = value;
        var ext = value.IndexOf("ext", StringComparison.OrdinalIgnoreCase);
        if (ext < 0) ext = value.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        if (ext > 0) mainPart = value[..ext];

        if (mainPart.Any(c => !(char.IsDigit(c) || c is ' ' or '-' or '.' or '(' or ')' or '+')))
            return false;

        var digits = new string(mainPart.Where(char.IsDigit).ToArray());
        if (digits.Length == 11 && digits[0] == '1') digits = digits[1..];
        return digits.Length == 10 && digits[0] is not ('0' or '1');
    }

    public static bool IsEmail(string value)
    {
        if (value.Length > 254 || value.Contains(' ')) return false;
        try
        {
            var addr = new MailAddress(value);
            return addr.Address == value && addr.Host.Contains('.');
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
