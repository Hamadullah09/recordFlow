using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Options;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Security;
using RecordFlow.Core.Workspaces;

namespace RecordFlow.Infrastructure.Csv;

/// <summary>
/// Validates and parses uploaded CSV files entirely in memory. The file is never written to disk
/// or to the database; the parsed rows become temporary working records.
/// </summary>
public sealed class CsvImportService(IOptions<CsvImportOptions> options, TimeProvider clock) : ICsvImportService
{
    private const int MaxHeaderLength = 100;
    private readonly CsvImportOptions _options = options.Value;

    public async Task<CsvImportResult> ImportAsync(Stream stream, string fileName, CancellationToken ct = default)
    {
        var result = new CsvImportResult();

        if (!string.Equals(Path.GetExtension(fileName), ".csv", StringComparison.OrdinalIgnoreCase))
        {
            result.Errors.Add("Only .csv files can be uploaded.");
            return result;
        }

        var bytes = await ReadBoundedAsync(stream, _options.MaxFileBytes, ct);
        if (bytes is null)
        {
            result.Errors.Add($"The file is larger than the {_options.MaxFileBytes / (1024 * 1024)} MB limit.");
            return result;
        }
        if (bytes.Length == 0)
        {
            result.Errors.Add("The file is empty.");
            return result;
        }
        if (LooksBinary(bytes))
        {
            result.Errors.Add("This file does not look like a CSV text file. If it is an Excel workbook, open it in Excel and use File › Save As › CSV.");
            return result;
        }

        var text = Decode(bytes, out var usedFallbackEncoding);
        if (usedFallbackEncoding)
            result.Warnings.Add("The file was not saved as UTF-8; special characters may not display exactly as in the original.");

        try
        {
            Parse(text, result, ct);
        }
        catch (CsvHelperException ex)
        {
            var row = ex.Context?.Parser?.Row;
            result.Errors.Add(row is > 0
                ? $"The file could not be read near line {row}. Please check that the file is a valid CSV."
                : "The file could not be read. Please check that the file is a valid CSV.");
        }

        return result;
    }

    private void Parse(string text, CsvImportResult result, CancellationToken ct)
    {
        var badDataRows = 0;
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            DetectDelimiter = true,
            DetectDelimiterValues = [",", ";", "\t", "|"],
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = _ => badDataRows++,
            IgnoreBlankLines = true,
            TrimOptions = TrimOptions.Trim,
        };

        using var reader = new StringReader(text);
        using var csv = new CsvReader(reader, config);

        if (!csv.Read() || !csv.ReadHeader() || csv.HeaderRecord is not { Length: > 0 } rawHeaders)
        {
            result.Errors.Add("The file has no header row. The first line must contain column names such as \"Contact ID\".");
            return;
        }

        if (rawHeaders.Length > _options.MaxColumns)
        {
            result.Errors.Add($"The file has {rawHeaders.Length} columns; the maximum is {_options.MaxColumns}.");
            return;
        }

        var headers = BuildHeaders(rawHeaders);
        if (headers.All(h => h.StartsWith("Column ", StringComparison.Ordinal)))
        {
            result.Errors.Add("The header row is empty. The first line must contain column names.");
            return;
        }
        result.Headers.AddRange(headers);

        var contactIdx = headers.FindIndex(h => HeaderNormalizer.ContactIdAliases.Contains(HeaderNormalizer.Normalize(h)));
        result.ContactIdHeaderIndex = contactIdx;
        if (contactIdx < 0)
            result.Warnings.Add("No \"Contact ID\" column was found, so temporary IDs (ROW-0001, ROW-0002, …) were assigned.");

        var now = clock.GetUtcNow();
        var generatedIds = 0;
        var extraCells = 0;

        while (csv.Read())
        {
            ct.ThrowIfCancellationRequested();

            if (result.Records.Count >= _options.MaxRows)
            {
                result.Errors.Add($"The file has more than {_options.MaxRows:N0} data rows. Please split it into smaller files.");
                result.Records.Clear();
                return;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < headers.Count; i++)
            {
                if (!csv.TryGetField<string>(i, out var raw)) continue;
                var clean = CsvSanitizer.CleanImportedValue(raw, _options.MaxValueLength);
                if (clean.Length > 0) values[headers[i]] = clean;
            }
            if (csv.Parser.Count > headers.Count) extraCells++;
            if (values.Count == 0) continue; // blank row

            var rowNumber = result.Records.Count + 1;
            string? contactId = contactIdx >= 0 && values.TryGetValue(headers[contactIdx], out var cid) ? cid : null;
            var generated = false;
            if (string.IsNullOrWhiteSpace(contactId))
            {
                contactId = $"ROW-{rowNumber:0000}";
                generated = true;
                if (contactIdx >= 0) generatedIds++;
            }

            result.Records.Add(new WorkingRecord
            {
                Key = SecureTokens.NewKey(),
                RowNumber = rowNumber,
                ContactId = contactId.Length > 100 ? contactId[..100] : contactId,
                ContactIdGenerated = generated,
                CreatedAtUtc = now,
                CsvValues = values,
            });
        }

        if (result.Records.Count == 0)
        {
            result.Errors.Add("The file has a header row but no data rows.");
            return;
        }

        if (generatedIds > 0)
            result.Warnings.Add($"{generatedIds} row(s) had no Contact ID, so temporary IDs were assigned.");
        if (extraCells > 0)
            result.Warnings.Add($"{extraCells} row(s) had more values than there are column headers; the extra values were ignored.");
        if (badDataRows > 0)
            result.Warnings.Add($"{badDataRows} value(s) had unbalanced quotation marks and were read as-is.");

        var duplicates = result.Records.Where(r => !r.ContactIdGenerated)
            .GroupBy(r => r.ContactId, StringComparer.OrdinalIgnoreCase)
            .Count(g => g.Count() > 1);
        if (duplicates > 0)
            result.Warnings.Add($"{duplicates} Contact ID(s) appear more than once in the file.");
    }

    private static List<string> BuildHeaders(string[] rawHeaders)
    {
        var headers = new List<string>(rawHeaders.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rawHeaders.Length; i++)
        {
            var h = CsvSanitizer.CleanImportedValue(rawHeaders[i], MaxHeaderLength);
            if (h.Length == 0 || HeaderNormalizer.Normalize(h).Length == 0) h = $"Column {i + 1}";
            var unique = h;
            for (var n = 2; !seen.Add(unique); n++) unique = $"{h} ({n})";
            headers.Add(unique);
        }
        return headers;
    }

    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>Rejects obvious binary formats (Excel/ZIP, PDF, executables) and files containing NUL bytes.</summary>
    private static bool LooksBinary(byte[] bytes)
    {
        ReadOnlySpan<byte> span = bytes;
        if (span.StartsWith("PK\x03\x04"u8) || span.StartsWith("%PDF"u8) || span.StartsWith("MZ"u8) ||
            span.StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }))
            return true;

        // UTF-16 files legitimately contain NULs; they are identified by their BOM.
        if (span.StartsWith(new byte[] { 0xFF, 0xFE }) || span.StartsWith(new byte[] { 0xFE, 0xFF }))
            return false;

        return span[..Math.Min(span.Length, 8192)].IndexOf((byte)0) >= 0;
    }

    private static string Decode(byte[] bytes, out bool usedFallback)
    {
        usedFallback = false;
        ReadOnlySpan<byte> span = bytes;
        if (span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (span.StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (span.StartsWith(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            usedFallback = true;
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
