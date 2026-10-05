using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using RecordFlow.Core.Abstractions;
using RecordFlow.Infrastructure.Services;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Pages.Csv;

/// <summary>
/// Receives the CSV upload. The file is read from the in-memory form buffer, parsed, and placed in the
/// user's temporary workspace; the file itself is never saved.
/// </summary>
[EnableRateLimiting(RateLimits.Upload)]
public class UploadModel(RecordWorkflowService workflow, IOptions<CsvImportOptions> options) : PortalPageModel
{
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/csv", "application/csv", "text/x-csv", "application/x-csv", "text/comma-separated-values",
        "application/vnd.ms-excel", "text/plain", "application/octet-stream", "",
    };

    public IActionResult OnGet() => RedirectToPage("/Dashboard");

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var maxBytes = options.Value.MaxFileBytes;
        var form = await Request.ReadFormAsync(ct);
        var file = form.Files.GetFile("csvFile");

        if (file is null || file.Length == 0)
            return Fail("Please choose a CSV file to upload.");
        if (file.Length > maxBytes)
            return Fail($"The file is larger than the {maxBytes / (1024 * 1024)} MB limit.");
        if (!string.Equals(Path.GetExtension(file.FileName), ".csv", StringComparison.OrdinalIgnoreCase))
            return Fail("Only .csv files can be uploaded. If your data is in Excel, use File › Save As › CSV.");
        if (!AllowedContentTypes.Contains(file.ContentType?.Split(';')[0].Trim() ?? ""))
            return Fail("That file type isn't supported. Please upload a .csv file.");

        await using var stream = file.OpenReadStream();
        var result = await workflow.ImportCsvAsync(UserId, stream, file.FileName, ct);
        if (!result.Succeeded)
            return Fail(string.Join(" ", result.Errors));

        FlashSuccess($"Imported {result.Records.Count} record{(result.Records.Count == 1 ? "" : "s")}. Select a record and click Proceed to continue.");
        return RedirectToPage("/Dashboard");
    }

    private RedirectToPageResult Fail(string message)
    {
        FlashError(message);
        return RedirectToPage("/Dashboard");
    }
}
