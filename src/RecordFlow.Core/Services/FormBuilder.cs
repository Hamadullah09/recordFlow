using RecordFlow.Core.Entities;
using RecordFlow.Core.Workspaces;

namespace RecordFlow.Core.Services;

/// <summary>
/// Builds the editable form for a working record from the configured field definitions and the
/// record's CSV values. Missing CSV columns simply produce empty fields; CSV columns that match no
/// definition are kept as editable "Additional Information" fields.
/// </summary>
public static class FormBuilder
{
    public const string AdditionalFieldPrefix = "csv_";

    public static List<WorkingField> Build(
        IEnumerable<FormFieldDefinition> definitions,
        WorkingRecord record,
        IReadOnlyList<string> headers,
        string? contactIdHeader)
    {
        var fields = new List<WorkingField>();
        var consumedHeaders = new HashSet<string>(StringComparer.Ordinal);
        if (contactIdHeader is not null) consumedHeaders.Add(contactIdHeader);

        var headerLookup = headers
            .GroupBy(HeaderNormalizer.Normalize)
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.First());

        var order = 0;
        foreach (var def in definitions.Where(d => d.IsActive).OrderBy(d => d.Section).ThenBy(d => d.DisplayOrder).ThenBy(d => d.Label))
        {
            string? csvValue = null;
            foreach (var alias in def.NormalizedAliases())
            {
                if (alias.Length == 0 || !headerLookup.TryGetValue(alias, out var header) || consumedHeaders.Contains(header))
                    continue;
                consumedHeaders.Add(header);
                csvValue ??= record.GetCsvValue(header);
                if (csvValue is not null) break;
            }

            fields.Add(new WorkingField
            {
                Key = def.Key,
                Label = def.Label,
                Section = def.Section,
                FieldType = def.FieldType,
                IsRequired = def.IsRequired,
                RecipientEditable = def.RecipientEditable,
                HelpText = def.HelpText,
                MaxLength = def.MaxLength > 0 ? def.MaxLength : 250,
                Options = OptionParser.Parse(def.Options).ToList(),
                Order = order++,
                CsvValue = csvValue,
                Value = csvValue,
                Source = csvValue is null ? FieldSource.Empty : FieldSource.Csv,
            });
        }

        var usedKeys = new HashSet<string>(fields.Select(f => f.Key), StringComparer.Ordinal);
        foreach (var header in headers)
        {
            if (consumedHeaders.Contains(header)) continue;
            var normalized = HeaderNormalizer.Normalize(header);
            if (normalized.Length == 0) continue;

            var key = AdditionalFieldPrefix + normalized;
            for (var i = 2; usedKeys.Contains(key); i++) key = $"{AdditionalFieldPrefix}{normalized}{i}";
            usedKeys.Add(key);

            var csvValue = record.GetCsvValue(header);
            fields.Add(new WorkingField
            {
                Key = key,
                Label = header,
                Section = FormSection.Additional,
                FieldType = FieldType.Text,
                RecipientEditable = true,
                MaxLength = 500,
                Order = order++,
                CsvValue = csvValue,
                Value = csvValue,
                Source = csvValue is null ? FieldSource.Empty : FieldSource.Csv,
            });
        }

        return fields;
    }

    /// <summary>Best-effort store name for display (form field first, then CSV).</summary>
    public static string? FindStoreName(WorkingRecord record, IReadOnlyList<string> headers)
    {
        var fromField = record.FindField("StoreName")?.Value;
        if (!string.IsNullOrWhiteSpace(fromField)) return fromField;
        foreach (var header in headers)
        {
            var n = HeaderNormalizer.Normalize(header);
            if (n is "storename" or "store" or "businessname" or "locationname")
                return record.GetCsvValue(header);
        }
        return null;
    }
}
