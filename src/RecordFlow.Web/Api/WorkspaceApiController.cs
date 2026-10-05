using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
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

    private string UserId => User.GetUserId();

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
