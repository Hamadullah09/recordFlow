using RecordFlow.Core.Entities;
using RecordFlow.Core.Workspaces;

namespace RecordFlow.Core.Services;

/// <summary>
/// Resolves the values of the six admin-managed columns for working records and decides which
/// columns end users see. A column is shown only when it is active, visible and has a value in
/// at least one record of the current working data (required columns are always shown so the
/// user can fill them in).
/// </summary>
public sealed class AdminColumnResolver
{
    private readonly IReadOnlyList<string> _headers;
    private readonly Dictionary<string, string> _headerLookup;
    private readonly Dictionary<string, FormFieldDefinition> _fieldLookup;

    public AdminColumnResolver(IReadOnlyList<string> headers, IEnumerable<FormFieldDefinition> formFields)
    {
        _headers = headers;
        _headerLookup = headers
            .GroupBy(HeaderNormalizer.Normalize)
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.First());

        _fieldLookup = new Dictionary<string, FormFieldDefinition>(StringComparer.Ordinal);
        foreach (var def in formFields)
        {
            _fieldLookup.TryAdd(HeaderNormalizer.Normalize(def.Key), def);
            _fieldLookup.TryAdd(HeaderNormalizer.Normalize(def.Label), def);
        }
    }

    public string? Resolve(AdminColumn column, WorkingRecord record)
    {
        if (record.ClosedAdminColumnValues is not null)
            return record.ClosedAdminColumnValues.TryGetValue(column.Slot, out var closed) ? NullIfBlank(closed) : null;

        if (record.AdminColumnValues.TryGetValue(column.Slot, out var entered) && !string.IsNullOrWhiteSpace(entered))
            return entered;

        if (!string.IsNullOrWhiteSpace(column.SourceField))
        {
            var source = HeaderNormalizer.Normalize(column.SourceField);

            // 1) The live form value (reflects edits made by the user or the recipient).
            if (record.Fields is not null && _fieldLookup.TryGetValue(source, out var def))
            {
                var fieldValue = record.FindField(def.Key)?.Value;
                if (!string.IsNullOrWhiteSpace(fieldValue)) return fieldValue;
            }

            // 2) A CSV header with the same name.
            if (_headerLookup.TryGetValue(source, out var header))
            {
                var csv = record.GetCsvValue(header);
                if (csv is not null) return csv;
            }

            // 3) Any CSV header that is an alias of the matching form field.
            if (_fieldLookup.TryGetValue(source, out def))
            {
                foreach (var alias in def.NormalizedAliases())
                {
                    if (_headerLookup.TryGetValue(alias, out var aliasHeader))
                    {
                        var csv = record.GetCsvValue(aliasHeader);
                        if (csv is not null) return csv;
                    }
                }
            }
        }

        return NullIfBlank(column.DefaultValue);
    }

    public IReadOnlyList<AdminColumn> VisibleColumns(IEnumerable<AdminColumn> columns, IReadOnlyCollection<WorkingRecord> records) =>
        columns
            .Where(c => c.IsActive && c.IsVisible)
            .Where(c => c.IsRequired || records.Any(r => Resolve(c, r) is not null))
            .OrderBy(c => c.DisplayOrder)
            .ThenBy(c => c.Slot)
            .ToList();

    public Dictionary<int, string> Snapshot(IEnumerable<AdminColumn> columns, WorkingRecord record)
    {
        var result = new Dictionary<int, string>();
        foreach (var c in columns.Where(c => c.IsActive))
        {
            var value = Resolve(c, record);
            if (value is not null) result[c.Slot] = value;
        }
        return result;
    }

    public IReadOnlyList<string> Headers => _headers;

    private static string? NullIfBlank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;
}
