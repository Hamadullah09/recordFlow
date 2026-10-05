using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Infrastructure.Data;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Pages.Account.Manage;

public class IndexModel(
    SignInManager<ApplicationUser> signIn,
    ApplicationDbContext db,
    IAuditLogger audit) : PortalPageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public string UserIdDisplay { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }

    public sealed class InputModel
    {
        [Required, StringLength(100, MinimumLength = 2), Display(Name = "User Name")]
        public string FullName { get; set; } = string.Empty;

        [Required, StringLength(200, MinimumLength = 2), Display(Name = "Company name")]
        public string CompanyName { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await LoadAsync();
        if (user is null) return Challenge();
        Input.FullName = user.FullName;
        Input.CompanyName = user.Company?.Name ?? string.Empty;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var user = await LoadAsync();
        if (user is null) return Challenge();
        if (!ModelState.IsValid) return Page();

        user.FullName = Input.FullName.Trim();
        var normalized = Company.Normalize(Input.CompanyName);
        if (user.Company?.NormalizedName != normalized)
        {
            var company = await db.Companies.FirstOrDefaultAsync(c => c.NormalizedName == normalized, ct)
                          ?? db.Companies.Add(new Company { Name = Input.CompanyName.Trim(), NormalizedName = normalized }).Entity;
            user.Company = company;
        }
        await db.SaveChangesAsync(ct);
        await signIn.RefreshSignInAsync(user);
        await audit.LogAsync(AuditCategories.Security, "ProfileUpdated", null, ct: ct);

        FlashSuccess("Your profile has been updated.");
        return RedirectToPage();
    }

    private async Task<ApplicationUser?> LoadAsync()
    {
        var user = await db.Users.Include(u => u.Company).FirstOrDefaultAsync(u => u.Id == UserId);
        if (user is null) return null;
        UserIdDisplay = user.UserName ?? string.Empty;
        Email = user.Email ?? string.Empty;
        CreatedAtUtc = user.CreatedAtUtc;
        return user;
    }
}
