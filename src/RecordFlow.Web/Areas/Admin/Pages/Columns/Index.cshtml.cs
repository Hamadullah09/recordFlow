using Microsoft.EntityFrameworkCore;
using RecordFlow.Core.Entities;
using RecordFlow.Infrastructure.Data;

namespace RecordFlow.Web.Areas.Admin.Pages.Columns;

public class IndexModel(ApplicationDbContext db) : AdminPageModel
{
    public IReadOnlyList<AdminColumn> Columns { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct) =>
        Columns = await db.AdminColumns.AsNoTracking().OrderBy(c => c.Slot).ToListAsync(ct);
}
