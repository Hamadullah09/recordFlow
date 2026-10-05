namespace RecordFlow.Core.Entities;

/// <summary>
/// One of the six administrator-managed dashboard columns. The six slots always exist;
/// everything else (label, type, visibility, value source) is configuration.
/// </summary>
public class AdminColumn
{
    public const int SlotCount = 6;

    public int Id { get; set; }

    /// <summary>Fixed position 1..6.</summary>
    public int Slot { get; set; }

    /// <summary>Internal name, used by administrators to identify the column.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Header shown to end users.</summary>
    public string DisplayLabel { get; set; } = string.Empty;

    public FieldType FieldType { get; set; } = FieldType.Text;
    public bool IsActive { get; set; }
    public bool IsVisible { get; set; } = true;

    /// <summary>
    /// Required columns must have a value before a record can go to checkout. Because the user
    /// has to be able to fill them in, required columns stay visible even when empty.
    /// </summary>
    public bool IsRequired { get; set; }

    /// <summary>Lets end users enter/override the value from the dashboard table.</summary>
    public bool AllowUserEdit { get; set; }

    public int DisplayOrder { get; set; }

    /// <summary>Optional CSV header or form field the value is read from (e.g. "Store Name").</summary>
    public string? SourceField { get; set; }

    /// <summary>Optional administrator-provided value applied when no other value exists.</summary>
    public string? DefaultValue { get; set; }

    /// <summary>Comma-separated options for <see cref="Core.FieldType.Select"/> columns.</summary>
    public string? Options { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }

    public IReadOnlyList<string> OptionList => OptionParser.Parse(Options);
}

public static class OptionParser
{
    public static IReadOnlyList<string> Parse(string? options) =>
        string.IsNullOrWhiteSpace(options)
            ? []
            : options.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .ToList();
}
