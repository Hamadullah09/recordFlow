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

namespace RecordFlow.Web.Areas.Admin.Pages.Records;

public class IndexModel(ApplicationDbContext db, IAuditLogger audit) : AdminPageModel
{
    private const int PageSize = 25;
    private const int MaxExportRows = 10_000;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }

    public IReadOnlyList<Row> Records { get; private set; } = [];
    public PagerModel Pager { get; private set; } = null!;

    public sealed record Row(Guid PublicId, string ConfirmationNumber, string ContactId, string? StoreName, string? CompanyName,
        string ConfirmedByName, DateTime ConfirmedAtUtc, string OrderNumber, decimal Total);

    public async Task OnGetAsync(int p = 1, CancellationToken ct = default)
    {
        var query = Filtered();
        var total = await query.CountAsync(ct);
        var (page, pages) = Paging.Normalize(p, total, PageSize);
        Records = await query.OrderByDescending(r => r.ConfirmedAtUtc).Skip((page - 1) * PageSize).Take(PageSize)
            .Select(r => new Row(r.PublicId, r.ConfirmationNumber, r.ContactId, r.StoreName, r.CompanyName, r.ConfirmedByName,
                r.ConfirmedAtUtc, r.Order!.OrderNumber, r.Order.Total))
            .ToListAsync(ct);
        Pager = new PagerModel(page, pages, total, RouteValues());
    }

    /// <summary>CSV export of finalized records. Every cell is neutralized against spreadsheet formula injection.</summary>
    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct)
    {
        var records = await Filtered().Include(r => r.Order).OrderByDescending(r => r.ConfirmedAtUtc).Take(MaxExportRows).ToListAsync(ct);

        var fieldLabels = records.SelectMany(r => r.GetFields().Select(f => f.Label)).Distinct().ToList();
        var columnLabels = records.SelectMany(r => r.GetAdminColumns().Select(f => f.Label)).Distinct().ToList();

        await using var buffer = new MemoryStream();
        await using (var writer = new StreamWriter(buffer, new UTF8Encoding(true), leaveOpen: true))
        await using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            string[] fixedHeaders = ["Confirmation Number", "Order Number", "Contact ID", "Store Name", "Company", "Response",
                "Amount", "Payment Status", "Transaction ID", "Confirmed By", "Confirmed At (UTC)", "Recipient Submitted At (UTC)"];
            var headers = fixedHeaders.Concat(columnLabels.Select(l => $"Dashboard: {l}")).Concat(fieldLabels);
            foreach (var h in headers) csv.WriteField(CsvSanitizer.EscapeForSpreadsheet(h));
            await csv.NextRecordAsync();

            foreach (var r in records)
            {
                var fields = r.GetFields().GroupBy(f => f.Label).ToDictionary(g => g.Key, g => g.First().Value);
                var cols = r.GetAdminColumns().GroupBy(f => f.Label).ToDictionary(g => g.Key, g => g.First().Value);
                string?[] values =
                [
                    r.ConfirmationNumber, r.Order?.OrderNumber, r.ContactId, r.StoreName, r.CompanyName, r.Response,
                    r.Order?.Total.ToString("0.00", CultureInfo.InvariantCulture), r.Order?.Status.ToString(), r.Order?.ProviderPaymentId,
                    r.ConfirmedByName, r.ConfirmedAtUtc.ToString("u"), r.RecipientSubmittedAtUtc?.ToString("u"),
                ];
                foreach (var v in values) csv.WriteField(CsvSanitizer.EscapeForSpreadsheet(v));
                foreach (var label in columnLabels) csv.WriteField(CsvSanitizer.EscapeForSpreadsheet(cols.GetValueOrDefault(label)));
                foreach (var label in fieldLabels) csv.WriteField(CsvSanitizer.EscapeForSpreadsheet(fields.GetValueOrDefault(label)));
                await csv.NextRecordAsync();
            }
        }

        await audit.LogAsync(AuditCategories.Admin, "RecordsExported", $"{records.Count} record(s)", ct: ct);
        return File(buffer.ToArray(), "text/csv", $"finalized-records-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
    }

    private IQueryable<FinalizedRecord> Filtered()
    {
        var query = db.FinalizedRecords.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim();
            query = query.Where(r => r.ContactId.Contains(q) || r.ConfirmationNumber.Contains(q) || (r.StoreName != null && r.StoreName.Contains(q)) ||
                                     (r.CompanyName != null && r.CompanyName.Contains(q)) || r.Order!.OrderNumber.Contains(q));
        }
        if (From is DateTime from) query = query.Where(r => r.ConfirmedAtUtc >= from.Date);
        if (To is DateTime to) query = query.Where(r => r.ConfirmedAtUtc < to.Date.AddDays(1));
        return query;
    }

    public Dictionary<string, string?> RouteValues() => new()
    {
        ["q"] = Q, ["from"] = From?.ToString("yyyy-MM-dd"), ["to"] = To?.ToString("yyyy-MM-dd"),
    };
}
