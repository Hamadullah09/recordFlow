using Microsoft.AspNetCore.Mvc;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Infrastructure.Services;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Pages.Records;

/// <summary>Step 4: the record is confirmed and waiting for its payment to complete.</summary>
public class PaymentModel(RecordWorkflowService workflow, LinkBuilder links) : RecordPageModel(workflow)
{
    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return RecordNotFound();
        if (Record.Status != RecordStatus.AwaitingPayment || Order is null) return RedirectToCurrentStep();
        return Page();
    }

    /// <summary>Opens a new payment page for the confirmed record (e.g. the previous one was closed).</summary>
    public async Task<IActionResult> OnPostRetryAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return RecordNotFound();
        if (Record.Status != RecordStatus.AwaitingPayment || Order is null) return RedirectToCurrentStep();

        var result = await Workflow.ConfirmAndPayAsync(UserId, Key, Order.PaymentMethod, DisplayName, order => new PaymentUrls(
            links.Absolute("/checkout/return?session_id={CHECKOUT_SESSION_ID}"),
            links.Absolute($"/checkout/cancel?order={order.PublicId}")), ct);
        return result.PaymentUrl is { } url ? Redirect(url) : RedirectToPage("/Receipts/Details", new { id = result.Order.PublicId });
    }

    /// <summary>Stops the pending payment and unlocks the record for editing.</summary>
    public async Task<IActionResult> OnPostCancelAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return RecordNotFound();
        if (Order is not null) await Workflow.CancelPaymentAsync(UserId, Order.PublicId, ct);
        FlashInfo("The payment was canceled and you were not charged. You can edit the record and confirm again.");
        return RedirectToPage("/Records/Open", new { key = Key });
    }
}
