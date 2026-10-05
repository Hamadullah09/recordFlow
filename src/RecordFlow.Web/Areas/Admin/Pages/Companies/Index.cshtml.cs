using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecordFlow.Infrastructure.Data;

namespace RecordFlow.Web.Areas.Admin.Pages.Companies;

public class IndexModel(ApplicationDbContext db) : AdminPageModel
{
    private const int PageSize = 25;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    public IReadOnlyList<Row> Companies { get; private set; } = [];
    public PagerModel Pager { get; private set; } = null!;

    public sealed record Row(int Id, string Name, string? City, string? State, string? Phone, bool IsActive, int Users, DateTime CreatedAtUtc);

    public async Task OnGetAsync(int p = 1, CancellationToken ct = default)
    {
        var query = db.Companies.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(Q)) query = query.Where(c => c.Name.Contains(Q.Trim()));
        var total = await query.CountAsync(ct);
        var (page, pages) = Paging.Normalize(p, total, PageSize);
        Companies = await query.OrderBy(c => c.Name).Skip((page - 1) * PageSize).Take(PageSize)
            .Select(c => new Row(c.Id, c.Name, c.City, c.State, c.Phone, c.IsActive, c.Users.Count, c.CreatedAtUtc))
            .ToListAsync(ct);
        Pager = new PagerModel(page, pages, total, new Dictionary<string, string?> { ["q"] = Q });
    }
}
