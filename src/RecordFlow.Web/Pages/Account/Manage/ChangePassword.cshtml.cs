using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Pages.Account.Manage;

[EnableRateLimiting(RateLimits.Auth)]
public class ChangePasswordModel(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, IAuditLogger audit) : PortalPageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();

    public sealed class InputModel
    {
        [Required, DataType(DataType.Password), Display(Name = "Current password")]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required, StringLength(128, MinimumLength = 10, ErrorMessage = "Password must be at least 10 characters."), DataType(DataType.Password), Display(Name = "New password")]
        public string NewPassword { get; set; } = string.Empty;

        [Required, DataType(DataType.Password), Display(Name = "Confirm new password")]
        [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return Page();
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();

        var result = await users.ChangePasswordAsync(user, Input.CurrentPassword, Input.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors)
                ModelState.AddModelError(e.Code == "PasswordMismatch" ? "Input.CurrentPassword" : "Input.NewPassword",
                    e.Code == "PasswordMismatch" ? "Your current password is incorrect." : e.Description);
            await audit.LogAsync(AuditCategories.Security, "PasswordChangeFailed", null, succeeded: false, ct: ct);
            return Page();
        }

        await signIn.RefreshSignInAsync(user);
        await audit.LogAsync(AuditCategories.Security, "PasswordChanged", null, ct: ct);
        FlashSuccess("Your password has been changed.");
        return RedirectToPage("./Index");
    }
}
