namespace RecordFlow.Core.Entities;

/// <summary>
/// Administrator-configurable definition of a field on the generated form. CSV columns are
/// matched to fields through <see cref="CsvAliases"/>; CSV columns that match no field are
/// still shown on the form under "Additional Information".
/// </summary>
public class FormFieldDefinition
{
    public int Id { get; set; }

    /// <summary>Stable machine key, e.g. "StoreName".</summary>
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
    public FormSection Section { get; set; }
    public FieldType FieldType { get; set; } = FieldType.Text;
    public bool IsRequired { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Whether the external recipient may edit this field on the shared form.</summary>
    public bool RecipientEditable { get; set; } = true;

    public int DisplayOrder { get; set; }

    /// <summary>Comma-separated CSV header names that map to this field.</summary>
    public string? CsvAliases { get; set; }

    public string? HelpText { get; set; }
    public int MaxLength { get; set; } = 250;

    /// <summary>Comma-separated options for drop-down fields.</summary>
    public string? Options { get; set; }

    public IEnumerable<string> NormalizedAliases()
    {
        yield return HeaderNormalizer.Normalize(Key);
        yield return HeaderNormalizer.Normalize(Label);
        foreach (var alias in OptionParser.Parse(CsvAliases))
            yield return HeaderNormalizer.Normalize(alias);
    }
}
