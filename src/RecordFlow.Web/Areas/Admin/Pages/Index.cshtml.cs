using Microsoft.EntityFrameworkCore;
using RecordFlow.Core;
using RecordFlow.Core.Entities;
using RecordFlow.Infrastructure.Data;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Areas.Admin.Pages;

/// <summary>Team overview: calling activity, outcomes, top callers, revenue and recent activity.</summary>
public class IndexModel(ApplicationDbContext db, TimeProvider clock, TimeDisplay time) : AdminPageModel
{
    private const int WindowDays = 30;

    public int CallsToday { get; private set; }
    public int CallsWeek { get; private set; }
    public double? InterestedRate { get; private set; }
    public double? AverageCallSeconds { get; private set; }
    public decimal RevenueMonth { get; private set; }
    public int PaidOrdersMonth { get; private set; }
    public int CompletedMonth { get; private set; }
    public int PendingOrders { get; private set; }
    public int ActiveCallers { get; private set; }
    public int UserCount { get; private set; }

    public IReadOnlyList<(CallOutcome Outcome, int Count)> Outcomes { get; private set; } = [];
    public int OutcomeTotal { get; private set; }
    public IReadOnlyList<CallerRow> TopCallers { get; private set; } = [];
    public IReadOnlyList<CallLog> RecentCalls { get; private set; } = [];
    public IReadOnlyList<AuditLog> RecentActivity { get; private set; } = [];
    public IReadOnlyList<Order> RecentOrders { get; private set; } = [];

    public sealed record CallerRow(string Name, int Calls, int Interested, int Completed, double? AverageSeconds);

    public async Task OnGetAsync(CancellationToken ct)
    {
        var nowOffset = clock.GetUtcNow();
        var now = nowOffset.UtcDateTime;
        var todayStart = time.StartOfLocalDayUtc(nowOffset);
        var weekStart = todayStart.AddDays(-6);
        var since = now.AddDays(-WindowDays);
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var recent = db.CallLogs.AsNoTracking().Where(c => c.StartedAtUtc >= since);
        CallsToday = await db.CallLogs.CountAsync(c => c.StartedAtUtc >= todayStart, ct);
        CallsWeek = await db.CallLogs.CountAsync(c => c.StartedAtUtc >= weekStart, ct);
        ActiveCallers = await db.CallLogs.Where(c => c.StartedAtUtc >= weekStart).Select(c => c.UserId).Distinct().CountAsync(ct);
        AverageCallSeconds = await recent.Where(c => c.EndedAtUtc != null)
            .AverageAsync(c => (double?)EF.Functions.DateDiffSecond(c.StartedAtUtc, c.EndedAtUtc!.Value), ct);

        var outcomes = await recent.Where(c => c.Outcome != null).GroupBy(c => c.Outcome!.Value)
            .Select(g => new { Outcome = g.Key, Count = g.Count() }).ToListAsync(ct);
        Outcomes = Enum.GetValues<CallOutcome>().Select(o => (o, outcomes.FirstOrDefault(x => x.Outcome == o)?.Count ?? 0)).ToList();
        OutcomeTotal = Outcomes.Sum(o => o.Count);
        InterestedRate = OutcomeTotal == 0 ? null : Outcomes.First(o => o.Outcome == CallOutcome.Interested).Count * 100.0 / OutcomeTotal;

        var perCaller = await recent.GroupBy(c => c.UserId).Select(g => new
        {
            UserId = g.Key,
            Calls = g.Count(),
            Interested = g.Count(c => c.Outcome == CallOutcome.Interested),
            AverageSeconds = g.Average(c => c.EndedAtUtc == null ? (double?)null : EF.Functions.DateDiffSecond(c.StartedAtUtc, c.EndedAtUtc.Value)),
        }).OrderByDescending(x => x.Calls).Take(8).ToListAsync(ct);
        var callerIds = perCaller.Select(x => x.UserId).ToList();
        var names = await db.Users.Where(u => callerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        var completed = await db.FinalizedRecords.Where(r => r.ConfirmedAtUtc >= since && callerIds.Contains(r.ConfirmedByUserId))
            .GroupBy(r => r.ConfirmedByUserId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        TopCallers = perCaller.Select(x => new CallerRow(names.GetValueOrDefault(x.UserId, "Unknown"), x.Calls, x.Interested,
            completed.GetValueOrDefault(x.UserId), x.AverageSeconds)).ToList();

        var paidThisMonth = db.Orders.Where(o => o.Status == OrderStatus.Paid && o.PaidAtUtc >= monthStart);
        PaidOrdersMonth = await paidThisMonth.CountAsync(ct);
        RevenueMonth = await paidThisMonth.SumAsync(o => (decimal?)o.Total, ct) ?? 0m;
        CompletedMonth = await db.FinalizedRecords.CountAsync(r => r.ConfirmedAtUtc >= monthStart, ct);
        PendingOrders = await db.Orders.CountAsync(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.Processing, ct);
        UserCount = await db.Users.CountAsync(ct);

        RecentCalls = await db.CallLogs.AsNoTracking().Include(c => c.User).OrderByDescending(c => c.StartedAtUtc).Take(8).ToListAsync(ct);
        RecentActivity = await db.AuditLogs.AsNoTracking().OrderByDescending(a => a.Id).Take(8).ToListAsync(ct);
        RecentOrders = await db.Orders.AsNoTracking().Include(o => o.User).OrderByDescending(o => o.CreatedAtUtc).Take(6).ToListAsync(ct);
    }

    public static string Duration(double? seconds) =>
        seconds is { } s ? RecordFlow.Web.Pages.DashboardModel.FormatDuration(TimeSpan.FromSeconds(Math.Round(s))) : "—";
}
