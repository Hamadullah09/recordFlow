using Microsoft.EntityFrameworkCore;
using RecordFlow.Core.Entities;
using RecordFlow.Infrastructure.Data;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Pages.Receipts;

public class IndexModel(ApplicationDbContext db) : PortalPageModel
{
    private const int PageSize = 20;

    public IReadOnlyList<Row> Orders { get; private set; } = [];
    public int PageNumber { get; private set; } = 1;
    public int TotalPages { get; private set; } = 1;

    public sealed record Row(Guid PublicId, string OrderNumber, string ContactId, string? StoreName, decimal Total,
        Core.OrderStatus Status, DateTime CreatedAtUtc, string? ConfirmationNumber);

    public async Task OnGetAsync(int p = 1, CancellationToken ct = default)
    {
        var query = db.Orders.AsNoTracking().Where(o => o.UserId == UserId);
        var total = await query.CountAsync(ct);
        TotalPages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        PageNumber = Math.Clamp(p, 1, TotalPages);
        Orders = await query
            .OrderByDescending(o => o.CreatedAtUtc)
            .Skip((PageNumber - 1) * PageSize).Take(PageSize)
            .Select(o => new Row(o.PublicId, o.OrderNumber, o.ContactId, o.StoreName, o.Total, o.Status, o.CreatedAtUtc,
                o.FinalizedRecord != null ? o.FinalizedRecord.ConfirmationNumber : null))
            .ToListAsync(ct);
    }
}
