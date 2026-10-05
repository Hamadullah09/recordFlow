using Microsoft.EntityFrameworkCore;
using RecordFlow.Core;
using RecordFlow.Core.Entities;
using RecordFlow.Infrastructure.Data;

namespace RecordFlow.Web.Areas.Admin.Pages;

public class IndexModel(ApplicationDbContext db, TimeProvider clock) : AdminPageModel
{
    public int UserCount { get; private set; }
    public int ActiveUsers30d { get; private set; }
    public int PaidOrdersMonth { get; private set; }
    public decimal RevenueMonth { get; private set; }
    public int FinalizedCount { get; private set; }
    public int PendingOrders { get; private set; }
    public IReadOnlyList<AuditLog> RecentActivity { get; private set; } = [];
    public IReadOnlyList<Order> RecentOrders { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var thirtyDaysAgo = now.AddDays(-30);

        UserCount = await db.Users.CountAsync(ct);
        ActiveUsers30d = await db.Users.CountAsync(u => u.LastLoginAtUtc >= thirtyDaysAgo, ct);
        var paidThisMonth = db.Orders.Where(o => o.Status == OrderStatus.Paid && o.PaidAtUtc >= monthStart);
        PaidOrdersMonth = await paidThisMonth.CountAsync(ct);
        RevenueMonth = await paidThisMonth.SumAsync(o => (decimal?)o.Total, ct) ?? 0m;
        FinalizedCount = await db.FinalizedRecords.CountAsync(ct);
        PendingOrders = await db.Orders.CountAsync(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.Processing, ct);
        RecentActivity = await db.AuditLogs.AsNoTracking().OrderByDescending(a => a.Id).Take(10).ToListAsync(ct);
        RecentOrders = await db.Orders.AsNoTracking().Include(o => o.User).OrderByDescending(o => o.CreatedAtUtc).Take(6).ToListAsync(ct);
    }
}
