using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RecordFlow.Infrastructure.Email;

namespace RecordFlow.Web.Pages.Dev;

/// <summary>Development-only viewer for emails captured by the development email sender. 404 elsewhere.</summary>
public partial class MailboxModel(IWebHostEnvironment env, IServiceProvider services) : PageModel
{
    public IReadOnlyList<DevMailMessage> Messages { get; private set; } = [];

    [GeneratedRegex(@"https?://[^\s""'<>]+")]
    private static partial Regex LinkRegex();

    public IActionResult OnGet()
    {
        var mailbox = env.IsDevelopment() ? services.GetService<DevMailbox>() : null;
        if (mailbox is null) return NotFound();
        Messages = mailbox.All();
        return Page();
    }

    public static IEnumerable<string> Links(string text) => LinkRegex().Matches(text).Select(m => m.Value).Distinct();
}
