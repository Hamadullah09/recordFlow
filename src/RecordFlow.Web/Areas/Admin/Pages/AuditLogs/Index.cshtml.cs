using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecordFlow.Core;
using RecordFlow.Core.Entities;
using RecordFlow.Infrastructure.Data;

namespace RecordFlow.Web.Areas.Admin.Pages.AuditLogs;

public class IndexModel(ApplicationDbContext db) : AdminPageModel
{
    private const int PageSize = 50;
    public static readonly string[] Categories =
        [AuditCategories.Security, AuditCategories.Admin, AuditCategories.Record, AuditCategories.Payment, AuditCategories.Workspace];

    [BindProperty(SupportsGet = true)] public string? Category { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public bool FailuresOnly { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }

    public IReadOnlyList<AuditLog> Entries { get; private set; } = [];
    public PagerModel Pager { get; private set; } = null!;

    public async Task OnGetAsync(int p = 1, CancellationToken ct = default)
    {
        var query = db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Category)) query = query.Where(a => a.Category == Category);
        if (FailuresOnly) query = query.Where(a => !a.Succeeded);
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim();
            query = query.Where(a => a.Action.Contains(q) || (a.UserName != null && a.UserName.Contains(q)) ||
                                     (a.EntityId != null && a.EntityId.Contains(q)) || (a.Details != null && a.Details.Contains(q)) ||
                                     (a.IpAddress != null && a.IpAddress.Contains(q)));
        }
        if (From is DateTime from) query = query.Where(a => a.TimestampUtc >= from.Date);
        if (To is DateTime to) query = query.Where(a => a.TimestampUtc < to.Date.AddDays(1));

        var total = await query.CountAsync(ct);
        var (page, pages) = Paging.Normalize(p, total, PageSize);
        Entries = await query.OrderByDescending(a => a.Id).Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(ct);
        Pager = new PagerModel(page, pages, total, new Dictionary<string, string?>
        {
            ["category"] = Category, ["q"] = Q, ["failuresOnly"] = FailuresOnly ? "true" : null,
            ["from"] = From?.ToString("yyyy-MM-dd"), ["to"] = To?.ToString("yyyy-MM-dd"),
        });
    }
}
