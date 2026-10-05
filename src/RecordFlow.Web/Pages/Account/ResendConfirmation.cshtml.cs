using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using RecordFlow.Core.Entities;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Pages.Account;

[EnableRateLimiting(RateLimits.Auth)]
public class ResendConfirmationModel(UserManager<ApplicationUser> users, AccountEmails accountEmails) : PageModel
{
    [BindProperty, Required, EmailAddress, Display(Name = "Email address")]
    public string Email { get; set; } = string.Empty;

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return Page();

        var user = await users.FindByEmailAsync(Email.Trim());
        if (user is { EmailConfirmed: false, IsDisabled: false })
        {
            try { await accountEmails.SendConfirmationAsync(user, ct); }
            catch (InvalidOperationException ex) { ModelState.AddModelError(string.Empty, ex.Message); return Page(); }
        }
        // Same response whether or not the account exists, to prevent account enumeration.
        return RedirectToPage("./CheckEmail", new { reason = "resend" });
    }
}
