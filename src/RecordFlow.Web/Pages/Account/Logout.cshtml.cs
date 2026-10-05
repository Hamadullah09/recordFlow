using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Infrastructure.Services;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Pages.Account;

public class LogoutModel(SignInManager<ApplicationUser> signIn, RecordWorkflowService workflow, IAuditLogger audit) : PageModel
{
    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? Page() : RedirectToPage("./Login");

    /// <summary>Signing out ends the working session: the temporary CSV workspace is deleted immediately.</summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            var userId = User.GetUserId();
            await workflow.EndSessionAsync(userId, "Signed out", ct);
            await audit.LogAsync(AuditCategories.Security, "Logout", null, ct: ct);
            await signIn.SignOutAsync();
        }
        TempData["Success"] = "You've been signed out and your temporary workspace was cleared.";
        return RedirectToPage("./Login");
    }
}
