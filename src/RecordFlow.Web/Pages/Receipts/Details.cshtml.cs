using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecordFlow.Core.Entities;
using RecordFlow.Infrastructure.Data;
using RecordFlow.Infrastructure.Services;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Pages.Receipts;

/// <summary>Step 11: final confirmation / receipt. Only the order's owner can open it.</summary>
public class DetailsModel(ApplicationDbContext db, OrderService orders) : PortalPageModel
{
    public Order Order { get; private set; } = null!;
    public FinalizedRecord? Finalized { get; private set; }
    public IReadOnlyList<IGrouping<string, FinalizedField>> Sections { get; private set; } = [];
    public IReadOnlyList<FinalizedField> Columns { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.FinalizedRecord)
            .FirstOrDefaultAsync(o => o.PublicId == id && o.UserId == UserId, ct);
        if (order is null) return NotFound();

        Order = await orders.SyncAsync(order, ct);
        Finalized = order.FinalizedRecord;
        if (Finalized is not null)
        {
            Sections = Finalized.GetFields().GroupBy(f => f.Section).ToList();
            Columns = Finalized.GetAdminColumns();
        }
        return Page();
    }
}
