using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Services;
using RecordFlow.Infrastructure.Services;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Api;

/// <summary>
/// JSON endpoints used by the dashboard and form pages. Every call is scoped to the signed-in user's own
/// workspace and protected by the antiforgery token sent in the RequestVerificationToken header.
/// </summary>
[ApiController]
[Route("api/workspace/records/{key}")]
[Authorize(Policy = Policies.PortalUser)]
[EnableRateLimiting(RateLimits.Api)]
[WorkflowApiExceptionFilter]
public class WorkspaceApiController(RecordWorkflowService workflow, LinkBuilder links, TimeDisplay time) : ControllerBase
{
    private static readonly HashSet<string> Channels = ["copy", "whatsapp", "sms", "native"];

    public sealed record ValueRequest(string? Value);
    public sealed record ShareLinkRequest(bool Regenerate);
    public sealed record ShareEmailRequest(string Email, string? Message);
    public sealed record ChannelRequest(string Channel);
    public sealed record OutcomeRequest(string? Outcome, string? Notes);

    private string UserId => User.GetUserId();

    /// <summary>Row clicked on the dashboard: starts the first call (if none yet) and returns the record's CSV details.</summary>
    [HttpPost("call/start")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartCall(string key, CancellationToken ct) =>
        Ok(CallDetails(await workflow.StartCallAsync(UserId, key, again: false, ct)));

    /// <summary>"Call again": starts a new call after the previous one ended.</summary>
    [HttpPost("call/again")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CallAgain(string key, CancellationToken ct) =>
        Ok(CallDetails(await workflow.StartCallAsync(UserId, key, again: true, ct)));

    /// <summary>"End call" in the record popup: stores the close time.</summary>
    [HttpPost("call/end")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EndCall(string key, CancellationToken ct) =>
        Ok(CallDetails(await workflow.EndCallAsync(UserId, key, ct)));

    /// <summary>Outcome and notes for the latest call.</summary>
    [HttpPost("call/outcome")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetCallOutcome(string key, OutcomeRequest request, CancellationToken ct)
    {
        CallOutcome? outcome = null;
        if (!string.IsNullOrWhiteSpace(request.Outcome))
        {
            if (!Enum.TryParse<CallOutcome>(request.Outcome, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                return Problem(title: "Choose a valid call outcome.", statusCode: StatusCodes.Status400BadRequest);
            outcome = parsed;
        }
        return Ok(CallDetails(await workflow.SetCallOutcomeAsync(UserId, key, outcome, request.Notes, ct)));
    }

    private object CallDetails(RecordContext ctx)
    {
        var r = ctx.Record;
        var last = r.LastCall;
        return new
        {
            contactId = r.ContactId,
            storeName = r.StoreName,
            status = r.Status.ToString(),
            statusLabel = r.Status.Name(),
            actionLabel = Pages.DashboardModel.ActionLabel(r),
            closed = r.IsClosed,
            callStarted = last?.StartedAtUtc.ToString("o"),
            callStartedText = time.Time(last?.StartedAtUtc),
            callStartedDate = time.Date(last?.StartedAtUtc),
            callEnded = last?.EndedAtUtc?.ToString("o"),
            callEndedText = time.Time(last?.EndedAtUtc),
            lastOutcome = last?.Outcome?.ToString(),
            lastNotes = last?.Notes,
            calls = r.Calls.Select((c, i) => new
            {
                number = i + 1,
                started = $"{time.Date(c.StartedAtUtc)} {time.Time(c.StartedAtUtc)}",
                ended = c.EndedAtUtc is null ? null : time.Time(c.EndedAtUtc),
                duration = c.Duration is { } d ? Pages.DashboardModel.FormatDuration(d) : null,
                outcome = c.Outcome?.Name(),
                outcomeClass = c.Outcome is { } o ? Pages.DashboardModel.OutcomeBadgeClass(o) : null,
                notes = c.Notes,
            }),
            // Every column of the uploaded row, in file order (empty after the record is completed and purged).
            // Valid U.S. phone numbers come with a tel: link for click-to-call.
            fields = ctx.Workspace.Headers.Select(h => new { label = h, value = r.GetCsvValue(h), tel = PhoneNumbers.ToTelUri(r.GetCsvValue(h)) }),
            phone = PhoneNumbers.Primary(r, ctx.Workspace.Headers) is { } p ? new { label = p.Label, number = p.Number, tel = p.TelUri } : null,
        };
    }

    [HttpPost("response")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetResponse(string key, ValueRequest request, CancellationToken ct)
    {
        await workflow.SetResponseAsync(UserId, key, request.Value, ct);
        return NoContent();
    }

    [HttpPost("columns/{slot:int:range(1,6)}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetColumn(string key, int slot, ValueRequest request, CancellationToken ct)
    {
        await workflow.SetAdminColumnValueAsync(UserId, key, slot, request.Value, ct);
        return NoContent();
    }

    [HttpPost("share-link")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimits.Share)]
    public async Task<IActionResult> ShareLink(string key, ShareLinkRequest request, CancellationToken ct)
    {
        var link = await workflow.GetOrCreateShareLinkAsync(UserId, key, request.Regenerate, ct);
        return Ok(new
        {
            url = links.ShareUrl(link.Token),
            expires = time.DateAndTime(link.ExpiresAtUtc),
            submitted = link.SubmittedAtUtc is not null,
        });
    }

    [HttpPost("share-email")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimits.Share)]
    public async Task<IActionResult> ShareEmail(string key, ShareEmailRequest request, CancellationToken ct)
    {
        await workflow.SendShareEmailAsync(UserId, key, request.Email ?? "", request.Message, links.ShareUrl, ct);
        return NoContent();
    }

    [HttpPost("share-channel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ShareChannel(string key, ChannelRequest request, CancellationToken ct)
    {
        var channel = request.Channel?.ToLowerInvariant() ?? "";
        if (!Channels.Contains(channel)) return BadRequest();
        var label = channel switch { "whatsapp" => "WhatsApp", "sms" => "Text message", "native" => "Device share", _ => "Copied link" };
        await workflow.RecordShareChannelAsync(UserId, key, label, ct);
        return NoContent();
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(string key, CancellationToken ct)
    {
        var ctx = await workflow.RequireRecordAsync(UserId, key, ct);
        var r = ctx.Record;
        return Ok(new
        {
            status = r.Status.ToString(),
            label = r.Status.Name(),
            recipientSubmitted = r.RecipientSubmittedAtUtc is not null,
            lastOpened = r.Share?.LastOpenedAtUtc is { } opened ? time.DateAndTime(opened) : null,
        });
    }
}

/// <summary>Maps workflow exceptions to RFC 7807 problem responses.</summary>
public sealed class WorkflowApiExceptionFilterAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        var (status, title) = context.Exception switch
        {
            WorkspaceExpiredException => (StatusCodes.Status410Gone, "Your working session has expired. Please upload your CSV again."),
            WorkflowException wf => (StatusCodes.Status400BadRequest, wf.Message),
            _ => (0, null),
        };
        if (title is null) return;

        context.Result = new ObjectResult(new ProblemDetails { Status = status, Title = title }) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
