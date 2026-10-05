using Microsoft.EntityFrameworkCore;
using RecordFlow.Core.Entities;
using RecordFlow.Infrastructure.Data;

namespace RecordFlow.Web.Areas.Admin.Pages.FormFields;

public class IndexModel(ApplicationDbContext db) : AdminPageModel
{
    public IReadOnlyList<FormFieldDefinition> Fields { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct) =>
        Fields = await db.FormFields.AsNoTracking().OrderBy(f => f.Section).ThenBy(f => f.DisplayOrder).ToListAsync(ct);
}
