using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using RecordFlow.Core;
using RecordFlow.Core.Workspaces;
using RecordFlow.Infrastructure.Services;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Pages.Recipient;

/// <summary>
/// Public form opened through a secure share link. The token is the only credential: it maps (by hash) to
/// exactly one working record, expires with the owner's session and stops working after one submission.
/// </summary>
[EnableRateLimiting(RateLimits.Recipient)]
public class FormModel(RecordWorkflowService workflow, LinkBuilder links) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Token { get; set; } = string.Empty;
    [BindProperty] public Dictionary<string, string?> Values { get; set; } = new();
    [BindProperty] public Dictionary<string, string?> Original { get; set; } = new();
    [BindProperty] public string? RecipientName { get; set; }

    public SharedFormContext? Shared { get; private set; }
    public IReadOnlyDictionary<string, string> Errors { get; private set; } = new Dictionary<string, string>();
    public bool AlreadySubmitted => Shared?.Record.Share?.SubmittedAtUtc is not null;

    public IEnumerable<IGrouping<FormSection, WorkingField>> Sections =>
        Shared!.Record.Fields!.OrderBy(f => f.Section).ThenBy(f => f.Order).GroupBy(f => f.Section);

    public string? ValueFor(WorkingField f) => Values.TryGetValue(f.Key, out var v) ? v : f.Value;
    public string? OriginalFor(WorkingField f) => Original.TryGetValue(f.Key, out var v) ? v : f.Value;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Shared = await workflow.GetSharedFormAsync(Token, markOpened: true, ct);
        if (Shared is null) Response.StatusCode = StatusCodes.Status404NotFound;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var result = await workflow.SubmitRecipientAsync(Token, Values, Original, RecipientName, links.RecordVerifyUrl, ct);
        switch (result.Outcome)
        {
            case RecipientSubmitOutcome.Submitted:
                return RedirectToPage("/Recipient/Done");
            case RecipientSubmitOutcome.ValidationFailed:
                Shared = await workflow.GetSharedFormAsync(Token, markOpened: false, ct);
                Errors = result.Errors;
                if (Shared is null) Response.StatusCode = StatusCodes.Status404NotFound;
                return Page();
            default:
                Shared = null;
                Response.StatusCode = StatusCodes.Status404NotFound;
                return Page();
        }
    }
}
