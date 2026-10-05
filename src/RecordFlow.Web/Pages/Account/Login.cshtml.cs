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
public class LoginModel(
    SignInManager<ApplicationUser> signIn,
    UserManager<ApplicationUser> users,
    IAuditLogger audit,
    TimeProvider clock) : PageModel
{
    private const string InvalidCredentials = "The User ID / email or password is incorrect.";

    [BindProperty] public InputModel Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public bool ShowResendLink { get; private set; }

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Enter your User ID or email address."), StringLength(254), Display(Name = "User ID or email address")]
        public string Login { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter your password."), DataType(DataType.Password), StringLength(128)]
        public string Password { get; set; } = string.Empty;
    }

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? LocalRedirect(SafeReturnUrl()) : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return Page();

        var login = Input.Login.Trim();
        var user = login.Contains('@') ? await users.FindByEmailAsync(login) : await users.FindByNameAsync(login);
        if (user is null)
        {
            await audit.LogAsync(AuditCategories.Security, "LoginFailed", "Unknown account", succeeded: false, userName: login, ct: ct);
            ModelState.AddModelError(string.Empty, InvalidCredentials);
            return Page();
        }

        var result = await signIn.PasswordSignInAsync(user, Input.Password, isPersistent: false, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            user.LastLoginAtUtc = clock.GetUtcNow().UtcDateTime;
            await users.UpdateAsync(user);
            await audit.LogAsync(AuditCategories.Security, "LoginSucceeded", null, userId: user.Id, userName: user.UserName, ct: ct);
            return LocalRedirect(SafeReturnUrl());
        }

        if (result.IsLockedOut)
        {
            await audit.LogAsync(AuditCategories.Security, user.IsDisabled ? "LoginBlockedDisabled" : "LoginLockedOut", null,
                succeeded: false, userId: user.Id, userName: user.UserName, ct: ct);
            if (user.IsDisabled)
            {
                ModelState.AddModelError(string.Empty, "This account has been disabled. Please contact your administrator.");
                return Page();
            }
            return RedirectToPage("./Lockout");
        }

        if (result.IsNotAllowed && await users.CheckPasswordAsync(user, Input.Password) && !user.EmailConfirmed)
        {
            ShowResendLink = true;
            ModelState.AddModelError(string.Empty, "Please confirm your email address before signing in. Check your inbox for the confirmation link.");
            return Page();
        }

        await audit.LogAsync(AuditCategories.Security, "LoginFailed", "Invalid password", succeeded: false, userId: user.Id, userName: user.UserName, ct: ct);
        ModelState.AddModelError(string.Empty, InvalidCredentials);
        return Page();
    }

    private string SafeReturnUrl() =>
        !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : Url.Page("/Dashboard")!;
}
