using RecordFlow.Core.Workspaces;

namespace RecordFlow.Core.Abstractions;

public interface ICsvImportService
{
    /// <summary>
    /// Validates and parses a CSV stream entirely in memory. Nothing is written to disk or to the
    /// database; the caller decides where (temporarily) to keep the result.
    /// </summary>
    Task<CsvImportResult> ImportAsync(Stream stream, string fileName, CancellationToken ct = default);
}

public sealed class CsvImportResult
{
    public bool Succeeded => Errors.Count == 0;
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> Headers { get; } = [];
    public int ContactIdHeaderIndex { get; set; } = -1;
    public List<WorkingRecord> Records { get; } = [];
}

public sealed class CsvImportOptions
{
    public const string SectionName = "CsvImport";

    public long MaxFileBytes { get; set; } = 5 * 1024 * 1024;
    public int MaxRows { get; set; } = 5000;
    public int MaxColumns { get; set; } = 100;
    public int MaxValueLength { get; set; } = 2000;
}
