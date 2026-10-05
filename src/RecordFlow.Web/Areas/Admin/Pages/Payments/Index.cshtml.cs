using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecordFlow.Core;
using RecordFlow.Infrastructure.Data;

namespace RecordFlow.Web.Areas.Admin.Pages.Payments;

public class IndexModel(ApplicationDbContext db) : AdminPageModel
{
    private const int PageSize = 25;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public OrderStatus? Status { get; set; }

    public IReadOnlyList<Row> Orders { get; private set; } = [];
    public PagerModel Pager { get; private set; } = null!;
    public decimal PaidTotal { get; private set; }

    public sealed record Row(Guid PublicId, string OrderNumber, DateTime CreatedAtUtc, string Customer, string? UserName, string ContactId,
        decimal Total, OrderStatus Status, DateTime? PaidAtUtc, string? ProviderPaymentId, string Provider, bool Finalized);

    public async Task OnGetAsync(int p = 1, CancellationToken ct = default)
    {
        var query = db.Orders.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim();
            query = query.Where(o => o.OrderNumber.Contains(q) || o.ContactId.Contains(q) || o.BillingName.Contains(q) ||
                                     o.BillingEmail.Contains(q) || (o.ProviderPaymentId != null && o.ProviderPaymentId.Contains(q)));
        }
        if (Status is OrderStatus s) query = query.Where(o => o.Status == s);

        var total = await query.CountAsync(ct);
        PaidTotal = await query.Where(o => o.Status == OrderStatus.Paid).SumAsync(o => (decimal?)o.Total, ct) ?? 0;
        var (page, pages) = Paging.Normalize(p, total, PageSize);
        Orders = await query.OrderByDescending(o => o.CreatedAtUtc).Skip((page - 1) * PageSize).Take(PageSize)
            .Select(o => new Row(o.PublicId, o.OrderNumber, o.CreatedAtUtc, o.BillingName, o.User != null ? o.User.UserName : null,
                o.ContactId, o.Total, o.Status, o.PaidAtUtc, o.ProviderPaymentId, o.Provider, o.FinalizedRecord != null))
            .ToListAsync(ct);
        Pager = new PagerModel(page, pages, total, new Dictionary<string, string?> { ["q"] = Q, ["status"] = Status?.ToString() });
    }

    public static string BadgeClass(OrderStatus s) => s switch
    {
        OrderStatus.Paid => "text-bg-success",
        OrderStatus.Pending or OrderStatus.Processing => "text-bg-warning",
        OrderStatus.Failed => "text-bg-danger",
        _ => "text-bg-light border",
    };
}
