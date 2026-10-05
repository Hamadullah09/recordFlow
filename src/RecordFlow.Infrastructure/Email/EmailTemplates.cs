using System.Net;

namespace RecordFlow.Infrastructure.Email;

/// <summary>Plain, mobile-friendly transactional email bodies. All dynamic values are HTML-encoded.</summary>
public static class EmailTemplates
{
    public sealed record EmailContent(string Subject, string Html, string Text);

    public static EmailContent ConfirmEmail(string appName, string name, string link) => Build(
        $"Confirm your {appName} account",
        $"Hi {name},",
        "Thanks for registering. Please confirm your email address to activate your account.",
        "Confirm email address", link,
        "If you did not create this account, you can ignore this email.");

    public static EmailContent ResetPassword(string appName, string name, string link) => Build(
        $"Reset your {appName} password",
        $"Hi {name},",
        "We received a request to reset your password. This link expires in 3 hours.",
        "Reset password", link,
        "If you did not request a password reset, you can ignore this email – your password will not change.");

    public static EmailContent SetPassword(string appName, string name, string link) => Build(
        $"Your {appName} account is ready",
        $"Hi {name},",
        "An administrator created an account for you. Choose a password to get started. This link expires in 3 hours.",
        "Set your password", link,
        "If you were not expecting this, please contact your administrator.");

    public static EmailContent ShareForm(string appName, string senderName, string? company, string storeName, string? note, string link, DateTimeOffset expires) => Build(
        $"Please complete the information for {storeName}",
        "Hello,",
        $"{senderName}{(string.IsNullOrWhiteSpace(company) ? "" : $" from {company}")} has asked you to review and complete the contact and emergency information for {storeName}." +
        (string.IsNullOrWhiteSpace(note) ? "" : $"\n\nMessage from {senderName}: \"{note}\""),
        "Open the secure form", link,
        $"This secure link expires on {expires:MMMM d, yyyy 'at' h:mm tt} UTC. Do not forward this email.");

    public static EmailContent RecipientSubmitted(string appName, string name, string contactId, string storeName, string link) => Build(
        $"Information received for {storeName}",
        $"Hi {name},",
        $"The recipient has completed the form for Contact ID {contactId} ({storeName}). Your record has been updated automatically and is ready for you to verify.",
        "Review and verify", link,
        "You will need to sign in to review the record.");

    private static EmailContent Build(string subject, string greeting, string body, string buttonText, string link, string footer)
    {
        static string e(string s) => WebUtility.HtmlEncode(s);
        var bodyHtml = string.Join("", body.Split("\n\n").Select(p => $"<p style=\"margin:0 0 16px\">{e(p)}</p>"));
        var html = $"""
            <!doctype html><html><body style="margin:0;background:#f4f6fb;font-family:Segoe UI,Helvetica,Arial,sans-serif;color:#1f2937">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td align="center" style="padding:32px 16px">
            <table role="presentation" width="100%" style="max-width:560px;background:#ffffff;border-radius:12px;padding:32px" cellpadding="0" cellspacing="0"><tr><td>
            <p style="margin:0 0 16px;font-size:16px">{e(greeting)}</p>
            {bodyHtml}
            <p style="margin:24px 0"><a href="{e(link)}" style="display:inline-block;background:#1d4ed8;color:#ffffff;text-decoration:none;padding:14px 22px;border-radius:8px;font-weight:600">{e(buttonText)}</a></p>
            <p style="margin:0 0 16px;font-size:13px;color:#6b7280">If the button does not work, copy this link into your browser:<br><span style="word-break:break-all">{e(link)}</span></p>
            <p style="margin:0;font-size:13px;color:#6b7280">{e(footer)}</p>
            </td></tr></table></td></tr></table></body></html>
            """;
        var text = $"{greeting}\n\n{body}\n\n{buttonText}: {link}\n\n{footer}";
        return new EmailContent(subject, html, text);
    }
}
