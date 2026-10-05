using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Pages.Account;

public class ConfirmEmailModel(UserManager<ApplicationUser> users, IAuditLogger audit) : PageModel
{
    public bool Succeeded { get; private set; }

    public async Task OnGetAsync(string? uid, string? code, CancellationToken ct)
    {
        var token = AccountEmails.Decode(code);
        if (string.IsNullOrEmpty(uid) || token is null) return;

        var user = await users.FindByIdAsync(uid);
        if (user is null) return;

        var result = await users.ConfirmEmailAsync(user, token);
        Succeeded = result.Succeeded;
        await audit.LogAsync(AuditCategories.Security, "EmailConfirmed", null, succeeded: Succeeded, userId: user.Id, userName: user.UserName, ct: ct);
    }
}
