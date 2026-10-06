using System.Globalization;
using System.Text;
using CsvHelper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Core.Security;
using RecordFlow.Infrastructure.Data;

namespace RecordFlow.Web.Areas.Admin.Pages.Calls;

/// <summary>Every call made by every caller, with outcome and notes (permanent call log).</summary>
public class IndexModel(ApplicationDbContext db, IAuditLogger audit) : AdminPageModel
{
    private const int PageSize = 50;
    private const int MaxExportRows = 50_000;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public CallOutcome? Outcome { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }

    public IReadOnlyList<Row> Calls { get; private set; } = [];
    public PagerModel Pager { get; private set; } = null!;

    public sealed record Row(DateTime StartedAtUtc, DateTime? EndedAtUtc, string Caller, string ContactId, string? StoreName,
        CallOutcome? Outcome, string? Notes, string? SourceFile)
    {
        public TimeSpan? Duration => EndedAtUtc - StartedAtUtc;
    }

    public async Task OnGetAsync(int p = 1, CancellationToken ct = default)
    {
        var query = Filtered();
        var total = await query.CountAsync(ct);
        var (page, pages) = Paging.Normalize(p, total, PageSize);
        Calls = await query.OrderByDescending(c => c.StartedAtUtc).Skip((page - 1) * PageSize).Take(PageSize)
            .Select(c => new Row(c.StartedAtUtc, c.EndedAtUtc, c.User!.FullName, c.ContactId, c.StoreName, c.Outcome, c.Notes, c.SourceFile))
            .ToListAsync(ct);
        Pager = new PagerModel(page, pages, total, RouteValues());
    }

    /// <summary>CSV export of the call log. Every cell is neutralized against spreadsheet formula injection.</summary>
    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct)
    {
        var calls = await Filtered().OrderByDescending(c => c.StartedAtUtc).Take(MaxExportRows)
            .Select(c => new { c.StartedAtUtc, c.EndedAtUtc, Caller = c.User!.FullName, CallerId = c.User.UserName, c.ContactId, c.StoreName, c.Outcome, c.Notes, c.SourceFile })
            .ToListAsync(ct);

        await using var buffer = new MemoryStream();
        await using (var writer = new StreamWriter(buffer, new UTF8Encoding(true), leaveOpen: true))
        await using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            string[] headers = ["Started (UTC)", "Ended (UTC)", "Duration (seconds)", "Caller", "Caller User ID", "Contact ID", "Store Name", "Outcome", "Notes", "Call list"];
            foreach (var h in headers) csv.WriteField(h);
            await csv.NextRecordAsync();

            foreach (var c in calls)
            {
                string?[] values =
                [
                    c.StartedAtUtc.ToString("u"), c.EndedAtUtc?.ToString("u"),
                    c.EndedAtUtc is { } end ? ((int)(end - c.StartedAtUtc).TotalSeconds).ToString(CultureInfo.InvariantCulture) : null,
                    c.Caller, c.CallerId, c.ContactId, c.StoreName, c.Outcome?.Name(), c.Notes, c.SourceFile,
                ];
                foreach (var v in values) csv.WriteField(CsvSanitizer.EscapeForSpreadsheet(v));
                await csv.NextRecordAsync();
            }
        }

        await audit.LogAsync(AuditCategories.Admin, "CallLogExported", $"{calls.Count} call(s)", ct: ct);
        return File(buffer.ToArray(), "text/csv", $"call-log-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
    }

    private IQueryable<CallLog> Filtered()
    {
        var query = db.CallLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim();
            query = query.Where(c => c.ContactId.Contains(q) || (c.StoreName != null && c.StoreName.Contains(q)) ||
                                     (c.Notes != null && c.Notes.Contains(q)) || c.User!.FullName.Contains(q) || c.User.UserName!.Contains(q));
        }
        if (Outcome is { } outcome) query = query.Where(c => c.Outcome == outcome);
        if (From is DateTime from) query = query.Where(c => c.StartedAtUtc >= from.Date);
        if (To is DateTime to) query = query.Where(c => c.StartedAtUtc < to.Date.AddDays(1));
        return query;
    }

    public Dictionary<string, string?> RouteValues() => new()
    {
        ["q"] = Q, ["outcome"] = Outcome?.ToString(), ["from"] = From?.ToString("yyyy-MM-dd"), ["to"] = To?.ToString("yyyy-MM-dd"),
    };
}
