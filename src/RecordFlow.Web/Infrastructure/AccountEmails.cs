using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Infrastructure.Email;

namespace RecordFlow.Web.Infrastructure;

/// <summary>Sends account emails containing single-purpose, expiring Identity tokens.</summary>
public sealed class AccountEmails(
    UserManager<ApplicationUser> users,
    IAppEmailSender email,
    LinkBuilder links,
    IOptions<AppOptions> app,
    ILogger<AccountEmails> logger)
{
    public async Task SendConfirmationAsync(ApplicationUser user, CancellationToken ct = default)
    {
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        var link = links.Absolute($"/Account/ConfirmEmail?uid={Uri.EscapeDataString(user.Id)}&code={Encode(token)}");
        var content = EmailTemplates.ConfirmEmail(app.Value.Name, user.FullName, link);
        await SendAsync(user, content, ct);
    }

    public async Task SendPasswordResetAsync(ApplicationUser user, bool newAccount, CancellationToken ct = default)
    {
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var link = links.Absolute($"/Account/ResetPassword?uid={Uri.EscapeDataString(user.Id)}&code={Encode(token)}");
        var content = newAccount
            ? EmailTemplates.SetPassword(app.Value.Name, user.FullName, link)
            : EmailTemplates.ResetPassword(app.Value.Name, user.FullName, link);
        await SendAsync(user, content, ct);
    }

    public static string Encode(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

    public static string? Decode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        try { return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code)); }
        catch (FormatException) { return null; }
    }

    private async Task SendAsync(ApplicationUser user, EmailTemplates.EmailContent content, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(user.Email)) return;
        try
        {
            await email.SendAsync(user.Email, content.Subject, content.Html, content.Text, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send account email '{Subject}'.", content.Subject);
            throw new InvalidOperationException("We couldn't send the email right now. Please try again shortly.", ex);
        }
    }
}
