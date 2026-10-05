using Microsoft.AspNetCore.Mvc;
using RecordFlow.Core;
using RecordFlow.Core.Entities;
using RecordFlow.Core.Workspaces;
using RecordFlow.Infrastructure.Services;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Pages.Records;

/// <summary>
/// Base for pages that operate on one working record. Records are only ever looked up inside the
/// signed-in user's own workspace, so another user's record key simply isn't found.
/// </summary>
public abstract class RecordPageModel(RecordWorkflowService workflow) : PortalPageModel
{
    protected RecordWorkflowService Workflow => workflow;

    [BindProperty(SupportsGet = true)] public string Key { get; set; } = string.Empty;

    public RecordContext Ctx { get; private set; } = null!;
    public WorkingRecord Record => Ctx.Record;
    public Order? Order => Ctx.Order;

    protected async Task<bool> LoadAsync(CancellationToken ct)
    {
        var ctx = await workflow.LoadRecordAsync(UserId, Key, ct);
        if (ctx is null) return false;
        Ctx = ctx;
        return true;
    }

    protected IActionResult RecordNotFound()
    {
        FlashError("That record isn't part of your current working session.");
        return RedirectToPage("/Dashboard");
    }

    /// <summary>Sends the user to the page for the record's current step.</summary>
    protected IActionResult RedirectToCurrentStep() => Record.Status switch
    {
        RecordStatus.Imported or RecordStatus.CheckoutStarted => RedirectToPage("/Records/Checkout", new { key = Key }),
        RecordStatus.AwaitingPayment or RecordStatus.Paid => RedirectToPage("/Records/Payment", new { key = Key }),
        RecordStatus.FormGenerated or RecordStatus.SharedPending => RedirectToPage("/Records/Form", new { key = Key }),
        RecordStatus.ReadyForVerification => RedirectToPage("/Records/Verify", new { key = Key }),
        _ when Record.OrderPublicId is Guid id => RedirectToPage("/Receipts/Details", new { id }),
        _ => RedirectToPage("/Dashboard"),
    };
}

public sealed record StepperModel(WorkingRecord Record, int Current);

public sealed record FieldInputModel(WorkingField Field, string? Value, string? Error, bool ReadOnly, string NamePrefix = "Values");
