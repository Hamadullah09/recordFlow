using System.Text;

namespace RecordFlow.Core;

/// <summary>
/// Normalizes CSV headers / field names so that "Store Phone Number", "store_phone_number"
/// and "StorePhoneNumber" all compare equal.
/// </summary>
public static class HeaderNormalizer
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>Header aliases that identify the Contact ID column.</summary>
    public static readonly IReadOnlySet<string> ContactIdAliases = new HashSet<string>(StringComparer.Ordinal)
    {
        "contactid", "contactidentifier", "contactnumber", "contactno", "contactnum", "contact",
    };
}
