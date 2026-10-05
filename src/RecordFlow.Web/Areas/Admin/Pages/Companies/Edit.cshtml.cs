using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Infrastructure.Data;

namespace RecordFlow.Web.Areas.Admin.Pages.Companies;

public class EditModel(ApplicationDbContext db, IAuditLogger audit) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    [BindProperty] public InputModel Input { get; set; } = new();
    public int UserCount { get; private set; }

    public sealed class InputModel
    {
        [Required, StringLength(200, MinimumLength = 2), Display(Name = "Company name")] public string Name { get; set; } = string.Empty;
        [StringLength(30), Phone] public string? Phone { get; set; }
        [StringLength(200), Display(Name = "Address line 1")] public string? AddressLine1 { get; set; }
        [StringLength(200), Display(Name = "Address line 2")] public string? AddressLine2 { get; set; }
        [StringLength(100)] public string? City { get; set; }
        [StringLength(2)] public string? State { get; set; }
        [StringLength(10), RegularExpression(@"^\d{5}(-\d{4})?$", ErrorMessage = "Enter a 5-digit ZIP code."), Display(Name = "ZIP code")] public string? ZipCode { get; set; }
        [Display(Name = "Active")] public bool IsActive { get; set; } = true;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (Id is int id)
        {
            var c = await db.Companies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return NotFound();
            Input = new InputModel
            {
                Name = c.Name, Phone = c.Phone, AddressLine1 = c.AddressLine1, AddressLine2 = c.AddressLine2,
                City = c.City, State = c.State, ZipCode = c.ZipCode, IsActive = c.IsActive,
            };
            UserCount = await db.Users.CountAsync(u => u.CompanyId == id, ct);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var normalized = Company.Normalize(Input.Name);
        if (await db.Companies.AnyAsync(c => c.NormalizedName == normalized && c.Id != Id, ct))
            ModelState.AddModelError("Input.Name", "A company with this name already exists.");
        if (!string.IsNullOrWhiteSpace(Input.State) && !UsStates.IsValid(Input.State))
            ModelState.AddModelError("Input.State", "Select a U.S. state.");
        if (!ModelState.IsValid) return Page();

        Company company;
        if (Id is int id)
        {
            company = await db.Companies.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new InvalidOperationException();
        }
        else
        {
            company = new Company();
            db.Companies.Add(company);
        }
        company.Name = Input.Name.Trim();
        company.NormalizedName = normalized;
        company.Phone = Clean(Input.Phone);
        company.AddressLine1 = Clean(Input.AddressLine1);
        company.AddressLine2 = Clean(Input.AddressLine2);
        company.City = Clean(Input.City);
        company.State = UsStates.Normalize(Input.State);
        company.ZipCode = Clean(Input.ZipCode);
        company.IsActive = Input.IsActive;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(AuditCategories.Admin, Id is null ? "CompanyCreated" : "CompanyUpdated", null, nameof(Company), company.Name, ct: ct);
        FlashSuccess("Company saved.");
        return RedirectToPage("./Index");
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
