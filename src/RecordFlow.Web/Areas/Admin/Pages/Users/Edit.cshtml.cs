using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Infrastructure.Data;
using RecordFlow.Infrastructure.Services;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Areas.Admin.Pages.Users;

/// <summary>Create (no id) or edit a user, manage roles, disable/enable, unlock and send password links.</summary>
public class EditModel(
    UserManager<ApplicationUser> users,
    ApplicationDbContext db,
    AccountEmails accountEmails,
    RecordWorkflowService workflow,
    IAuditLogger audit,
    TimeProvider clock) : AdminPageModel
{
    [BindProperty(SupportsGet = true)] public string? Id { get; set; }
    [BindProperty] public InputModel Input { get; set; } = new();

    public ApplicationUser? Existing { get; private set; }
    public IEnumerable<SelectListItem> Companies { get; private set; } = [];
    public bool IsSelf => Id is not null && Id == AdminId;
    public bool IsLockedOut => Existing is { IsDisabled: false, LockoutEnd: { } end } && end > clock.GetUtcNow();

    public sealed class InputModel
    {
        [Required, StringLength(100, MinimumLength = 2), Display(Name = "User Name")]
        public string FullName { get; set; } = string.Empty;

        [Required, StringLength(32, MinimumLength = 4), Display(Name = "User ID")]
        [RegularExpression(@"^[A-Za-z0-9._-]+$", ErrorMessage = "User ID may contain only letters, numbers, periods, hyphens and underscores.")]
        public string UserId { get; set; } = string.Empty;

        [Required, EmailAddress, StringLength(254), Display(Name = "Email address")]
        public string Email { get; set; } = string.Empty;

        [Display(Name = "Company")]
        public int? CompanyId { get; set; }

        public bool RoleUser { get; set; } = true;
        public bool RoleAdministrator { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (Id is not null)
        {
            Existing = await users.FindByIdAsync(Id);
            if (Existing is null) return NotFound();
            var roles = await users.GetRolesAsync(Existing);
            Input = new InputModel
            {
                FullName = Existing.FullName, UserId = Existing.UserName ?? "", Email = Existing.Email ?? "",
                CompanyId = Existing.CompanyId,
                RoleUser = roles.Contains(Roles.User), RoleAdministrator = roles.Contains(Roles.Administrator),
            };
        }
        await LoadCompaniesAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Existing = Id is null ? null : await users.FindByIdAsync(Id);
        if (Id is not null && Existing is null) return NotFound();

        if (!Input.RoleUser && !Input.RoleAdministrator)
            ModelState.AddModelError(string.Empty, "Select at least one role.");
        if (IsSelf && !Input.RoleAdministrator)
            ModelState.AddModelError(string.Empty, "You can't remove your own Administrator role.");
        if (Input.CompanyId is int cid && !await db.Companies.AnyAsync(c => c.Id == cid, ct))
            ModelState.AddModelError("Input.CompanyId", "Select a valid company.");

        var byName = await users.FindByNameAsync(Input.UserId.Trim());
        if (byName is not null && byName.Id != Id) ModelState.AddModelError("Input.UserId", "That User ID is already taken.");
        var byEmail = await users.FindByEmailAsync(Input.Email.Trim());
        if (byEmail is not null && byEmail.Id != Id) ModelState.AddModelError("Input.Email", "Another account already uses this email.");

        if (!ModelState.IsValid)
        {
            await LoadCompaniesAsync(ct);
            return Page();
        }

        var user = Existing ?? new ApplicationUser();
        var emailChanged = !string.Equals(user.Email, Input.Email.Trim(), StringComparison.OrdinalIgnoreCase);
        user.FullName = Input.FullName.Trim();
        user.UserName = Input.UserId.Trim();
        user.Email = Input.Email.Trim();
        user.CompanyId = Input.CompanyId;

        IdentityResult result;
        if (Existing is null)
        {
            // Created without a password: the user chooses one through an emailed, expiring link.
            result = await users.CreateAsync(user);
        }
        else
        {
            if (emailChanged) user.EmailConfirmed = true; // changed by an administrator
            result = await users.UpdateAsync(user);
        }
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors) ModelState.AddModelError(string.Empty, e.Description);
            await LoadCompaniesAsync(ct);
            return Page();
        }

        await SyncRoleAsync(user, Roles.User, Input.RoleUser);
        await SyncRoleAsync(user, Roles.Administrator, Input.RoleAdministrator);

        if (Existing is null)
        {
            await audit.LogAsync(AuditCategories.Admin, "UserCreated", $"Roles: {RolesText()}", nameof(ApplicationUser), user.UserName, ct: ct);
            try
            {
                await accountEmails.SendPasswordResetAsync(user, newAccount: true, ct);
                FlashSuccess($"User {user.UserName} created. An email was sent so they can choose a password.");
            }
            catch (InvalidOperationException)
            {
                FlashError($"User {user.UserName} was created, but the set-password email could not be sent. Use “Send password link” to retry.");
            }
        }
        else
        {
            await audit.LogAsync(AuditCategories.Admin, "UserUpdated", $"Roles: {RolesText()}{(emailChanged ? "; email changed" : "")}", nameof(ApplicationUser), user.UserName, ct: ct);
            FlashSuccess("User updated.");
        }
        return RedirectToPage(new { id = user.Id });
    }

    public async Task<IActionResult> OnPostDisableAsync(CancellationToken ct)
    {
        var user = Id is null ? null : await users.FindByIdAsync(Id);
        if (user is null) return NotFound();
        if (IsSelf) { FlashError("You can't disable your own account."); return RedirectToPage(new { id = Id }); }

        user.IsDisabled = true;
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        await users.UpdateAsync(user);
        await users.UpdateSecurityStampAsync(user); // signs the user out everywhere within minutes
        await workflow.EndSessionAsync(user.Id, "Account disabled by administrator", ct);
        await audit.LogAsync(AuditCategories.Admin, "UserDisabled", null, nameof(ApplicationUser), user.UserName, ct: ct);
        FlashSuccess($"{user.UserName} has been disabled and signed out.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostEnableAsync(CancellationToken ct)
    {
        var user = Id is null ? null : await users.FindByIdAsync(Id);
        if (user is null) return NotFound();
        user.IsDisabled = false;
        user.LockoutEnd = null;
        user.AccessFailedCount = 0;
        await users.UpdateAsync(user);
        await audit.LogAsync(AuditCategories.Admin, "UserEnabled", null, nameof(ApplicationUser), user.UserName, ct: ct);
        FlashSuccess($"{user.UserName} has been enabled.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostUnlockAsync(CancellationToken ct)
    {
        var user = Id is null ? null : await users.FindByIdAsync(Id);
        if (user is null) return NotFound();
        if (!user.IsDisabled)
        {
            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
            await audit.LogAsync(AuditCategories.Admin, "UserUnlocked", null, nameof(ApplicationUser), user.UserName, ct: ct);
            FlashSuccess($"{user.UserName} can sign in again.");
        }
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostSendResetAsync(CancellationToken ct)
    {
        var user = Id is null ? null : await users.FindByIdAsync(Id);
        if (user is null) return NotFound();
        try
        {
            await accountEmails.SendPasswordResetAsync(user, newAccount: !await users.HasPasswordAsync(user), ct);
            await audit.LogAsync(AuditCategories.Admin, "PasswordLinkSent", null, nameof(ApplicationUser), user.UserName, ct: ct);
            FlashSuccess($"A password link was emailed to {user.Email}.");
        }
        catch (InvalidOperationException ex)
        {
            FlashError(ex.Message);
        }
        return RedirectToPage(new { id = Id });
    }

    private async Task SyncRoleAsync(ApplicationUser user, string role, bool shouldHave)
    {
        var has = await users.IsInRoleAsync(user, role);
        if (shouldHave && !has) await users.AddToRoleAsync(user, role);
        else if (!shouldHave && has) await users.RemoveFromRoleAsync(user, role);
    }

    private string RolesText() =>
        string.Join(", ", new[] { Input.RoleUser ? Roles.User : null, Input.RoleAdministrator ? Roles.Administrator : null }.OfType<string>());

    private async Task LoadCompaniesAsync(CancellationToken ct) =>
        Companies = await db.Companies.AsNoTracking().OrderBy(c => c.Name)
            .Select(c => new SelectListItem(c.IsActive ? c.Name : c.Name + " (inactive)", c.Id.ToString()))
            .ToListAsync(ct);
}
