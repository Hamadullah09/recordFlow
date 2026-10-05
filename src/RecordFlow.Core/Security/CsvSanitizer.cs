using System.Text;

namespace RecordFlow.Core.Security;

/// <summary>
/// Defends against CSV/formula injection. Imported values are cleaned of control characters;
/// exported values that a spreadsheet would treat as formulas are neutralized.
/// </summary>
public static class CsvSanitizer
{
    private static readonly char[] FormulaTriggers = ['=', '+', '-', '@', '\t', '\r', '＝', '＋', '－', '＠'];

    /// <summary>Removes control characters (except tab/newline) and trims the value.</summary>
    public static string CleanImportedValue(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var ch in value)
        {
            if (char.IsControl(ch) && ch is not ('\n' or '\t')) continue;
            if (ch is '​' or '﻿') continue; // zero-width space / stray BOM
            sb.Append(ch);
            if (sb.Length >= maxLength) break;
        }
        return sb.ToString().Trim();
    }

    /// <summary>Prefixes a quote so spreadsheet apps show the value as text instead of evaluating it.</summary>
    public static string EscapeForSpreadsheet(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return Array.IndexOf(FormulaTriggers, value[0]) >= 0 ? "'" + value : value;
    }
}
