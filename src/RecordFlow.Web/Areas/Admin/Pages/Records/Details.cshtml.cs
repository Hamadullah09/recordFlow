using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecordFlow.Core.Entities;
using RecordFlow.Infrastructure.Data;

namespace RecordFlow.Web.Areas.Admin.Pages.Records;

public class DetailsModel(ApplicationDbContext db) : AdminPageModel
{
    public FinalizedRecord Record { get; private set; } = null!;
    public string? OwnerName { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var record = await db.FinalizedRecords.AsNoTracking().Include(r => r.Order).FirstOrDefaultAsync(r => r.PublicId == id, ct);
        if (record is null) return NotFound();
        Record = record;
        OwnerName = await db.Users.Where(u => u.Id == record.UserId).Select(u => u.FullName + " (" + u.UserName + ")").FirstOrDefaultAsync(ct);
        return Page();
    }
}
