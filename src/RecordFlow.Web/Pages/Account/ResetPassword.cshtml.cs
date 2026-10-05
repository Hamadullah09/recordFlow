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
public class ResetPasswordModel(UserManager<ApplicationUser> users, IAuditLogger audit) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Uid { get; set; }
    [BindProperty(SupportsGet = true)] public string? Code { get; set; }
    [BindProperty] public InputModel Input { get; set; } = new();

    public sealed class InputModel
    {
        [Required, StringLength(128, MinimumLength = 10, ErrorMessage = "Password must be at least 10 characters."), DataType(DataType.Password), Display(Name = "New password")]
        public string Password { get; set; } = string.Empty;

        [Required, DataType(DataType.Password), Display(Name = "Confirm new password")]
        [Compare(nameof(Password), ErrorMessage = "Passwords do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public IActionResult OnGet() =>
        string.IsNullOrEmpty(Uid) || string.IsNullOrEmpty(Code) ? RedirectToPage("./ForgotPassword") : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return Page();

        var token = AccountEmails.Decode(Code);
        var user = string.IsNullOrEmpty(Uid) ? null : await users.FindByIdAsync(Uid);
        if (user is null || token is null || user.IsDisabled)
        {
            ModelState.AddModelError(string.Empty, "This reset link is invalid or has expired. Please request a new one.");
            return Page();
        }

        var result = await users.ResetPasswordAsync(user, token, Input.Password);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors)
                ModelState.AddModelError(e.Code == "InvalidToken" ? string.Empty : "Input.Password",
                    e.Code == "InvalidToken" ? "This reset link is invalid or has expired. Please request a new one." : e.Description);
            return Page();
        }

        // The emailed link proves ownership of the address, so it also confirms it.
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await users.UpdateAsync(user);
        }
        await users.ResetAccessFailedCountAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        await audit.LogAsync(AuditCategories.Security, "PasswordReset", null, userId: user.Id, userName: user.UserName, ct: ct);

        TempData["Success"] = "Your password has been updated. You can sign in now.";
        return RedirectToPage("./Login");
    }
}
