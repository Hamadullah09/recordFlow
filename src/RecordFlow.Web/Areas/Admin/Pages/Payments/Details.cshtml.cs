using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Infrastructure.Data;
using RecordFlow.Infrastructure.Services;

namespace RecordFlow.Web.Areas.Admin.Pages.Payments;

public class DetailsModel(ApplicationDbContext db, OrderService orders, IAuditLogger audit) : AdminPageModel
{
    public Order Order { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var order = await Load(id, ct);
        if (order is null) return NotFound();
        Order = order;
        return Page();
    }

    /// <summary>Re-checks a pending/processing payment with the provider (e.g. if a webhook was missed).</summary>
    public async Task<IActionResult> OnPostRefreshAsync(Guid id, CancellationToken ct)
    {
        var order = await Load(id, ct);
        if (order is null) return NotFound();
        var before = order.Status;
        await orders.SyncAsync(order, ct);
        await audit.LogAsync(AuditCategories.Admin, "PaymentStatusRefreshed", $"{before} → {order.Status}", nameof(Order), order.OrderNumber, ct: ct);
        FlashSuccess(before == order.Status ? $"No change: payment is {order.Status.Name()}." : $"Payment status updated to {order.Status.Name()}.");
        return RedirectToPage(new { id });
    }

    private Task<Order?> Load(Guid id, CancellationToken ct) =>
        db.Orders.Include(o => o.User).Include(o => o.FinalizedRecord).FirstOrDefaultAsync(o => o.PublicId == id, ct);
}
