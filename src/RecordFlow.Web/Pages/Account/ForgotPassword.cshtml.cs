using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Pages.Account;

[EnableRateLimiting(RateLimits.Auth)]
public class ForgotPasswordModel(UserManager<ApplicationUser> users, AccountEmails accountEmails, IAuditLogger audit) : PageModel
{
    [BindProperty, Required(ErrorMessage = "Enter your User ID or email address."), StringLength(254), Display(Name = "User ID or email address")]
    public string Login { get; set; } = string.Empty;

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return Page();

        var login = Login.Trim();
        var user = login.Contains('@') ? await users.FindByEmailAsync(login) : await users.FindByNameAsync(login);
        if (user is { IsDisabled: false })
        {
            try
            {
                await accountEmails.SendPasswordResetAsync(user, newAccount: false, ct);
                await audit.LogAsync(AuditCategories.Security, "PasswordResetRequested", null, userId: user.Id, userName: user.UserName, ct: ct);
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                return Page();
            }
        }
        // Identical response for unknown accounts prevents account enumeration.
        return RedirectToPage("./CheckEmail", new { reason = "reset" });
    }
}
