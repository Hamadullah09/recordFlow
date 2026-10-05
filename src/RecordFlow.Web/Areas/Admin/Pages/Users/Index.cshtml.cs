using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecordFlow.Core;
using RecordFlow.Infrastructure.Data;

namespace RecordFlow.Web.Areas.Admin.Pages.Users;

public class IndexModel(ApplicationDbContext db, TimeProvider clock) : AdminPageModel
{
    private const int PageSize = 25;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Role { get; set; }

    public IReadOnlyList<Row> Users { get; private set; } = [];
    public PagerModel Pager { get; private set; } = null!;

    public sealed record Row(string Id, string FullName, string? UserName, string? Email, string? Company, bool IsDisabled,
        bool EmailConfirmed, DateTimeOffset? LockoutEnd, DateTime? LastLoginAtUtc, DateTime CreatedAtUtc, List<string> Roles);

    public async Task OnGetAsync(int p = 1, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var query = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(Q))
        {
            var q = Q.Trim();
            query = query.Where(u => u.FullName.Contains(q) || u.UserName!.Contains(q) || u.Email!.Contains(q) ||
                                     (u.Company != null && u.Company.Name.Contains(q)));
        }
        query = Status switch
        {
            "active" => query.Where(u => !u.IsDisabled),
            "disabled" => query.Where(u => u.IsDisabled),
            "unconfirmed" => query.Where(u => !u.EmailConfirmed),
            "locked" => query.Where(u => !u.IsDisabled && u.LockoutEnd != null && u.LockoutEnd > now),
            _ => query,
        };
        if (Role is Roles.Administrator or Roles.User)
        {
            var roleId = await db.Roles.Where(r => r.Name == Role).Select(r => r.Id).FirstOrDefaultAsync(ct);
            query = query.Where(u => db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == roleId));
        }

        var total = await query.CountAsync(ct);
        var (page, pages) = Paging.Normalize(p, total, PageSize);
        Users = await query
            .OrderBy(u => u.FullName)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(u => new Row(u.Id, u.FullName, u.UserName, u.Email, u.Company != null ? u.Company.Name : null, u.IsDisabled,
                u.EmailConfirmed, u.LockoutEnd, u.LastLoginAtUtc, u.CreatedAtUtc,
                db.UserRoles.Where(ur => ur.UserId == u.Id).Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name!).ToList()))
            .ToListAsync(ct);

        Pager = new PagerModel(page, pages, total, new Dictionary<string, string?> { ["q"] = Q, ["status"] = Status, ["role"] = Role });
    }

    public bool IsLocked(Row u) => !u.IsDisabled && u.LockoutEnd > clock.GetUtcNow();
}
