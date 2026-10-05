namespace RecordFlow.Core;

public static class UsStates
{
    public static readonly IReadOnlyList<KeyValuePair<string, string>> All =
    [
        new("AL", "Alabama"), new("AK", "Alaska"), new("AZ", "Arizona"), new("AR", "Arkansas"),
        new("CA", "California"), new("CO", "Colorado"), new("CT", "Connecticut"), new("DE", "Delaware"),
        new("DC", "District of Columbia"), new("FL", "Florida"), new("GA", "Georgia"), new("HI", "Hawaii"),
        new("ID", "Idaho"), new("IL", "Illinois"), new("IN", "Indiana"), new("IA", "Iowa"),
        new("KS", "Kansas"), new("KY", "Kentucky"), new("LA", "Louisiana"), new("ME", "Maine"),
        new("MD", "Maryland"), new("MA", "Massachusetts"), new("MI", "Michigan"), new("MN", "Minnesota"),
        new("MS", "Mississippi"), new("MO", "Missouri"), new("MT", "Montana"), new("NE", "Nebraska"),
        new("NV", "Nevada"), new("NH", "New Hampshire"), new("NJ", "New Jersey"), new("NM", "New Mexico"),
        new("NY", "New York"), new("NC", "North Carolina"), new("ND", "North Dakota"), new("OH", "Ohio"),
        new("OK", "Oklahoma"), new("OR", "Oregon"), new("PA", "Pennsylvania"), new("RI", "Rhode Island"),
        new("SC", "South Carolina"), new("SD", "South Dakota"), new("TN", "Tennessee"), new("TX", "Texas"),
        new("UT", "Utah"), new("VT", "Vermont"), new("VA", "Virginia"), new("WA", "Washington"),
        new("WV", "West Virginia"), new("WI", "Wisconsin"), new("WY", "Wyoming"),
        new("PR", "Puerto Rico"), new("GU", "Guam"), new("VI", "U.S. Virgin Islands"),
        new("AS", "American Samoa"), new("MP", "Northern Mariana Islands"),
    ];

    private static readonly Dictionary<string, string> ByCode =
        All.ToDictionary(s => s.Key, s => s.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> ByName =
        All.ToDictionary(s => s.Value, s => s.Key, StringComparer.OrdinalIgnoreCase);

    public static bool IsValid(string? value) => Normalize(value) is not null;

    /// <summary>Returns the two-letter code for a code or full state name, or null.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        return ByCode.TryGetValue(v, out var code) ? code : ByName.TryGetValue(v, out code) ? code : null;
    }
}
