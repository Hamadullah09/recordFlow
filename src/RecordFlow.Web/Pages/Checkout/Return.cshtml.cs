using Microsoft.AspNetCore.Mvc;
using RecordFlow.Core;
using RecordFlow.Infrastructure.Services;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Pages.Checkout;

/// <summary>
/// Where the payment provider sends the customer back. The query string is never trusted: the order is
/// looked up for the signed-in user and its status is re-verified with the provider.
/// </summary>
public class ReturnModel(RecordWorkflowService workflow) : PortalPageModel
{
    public async Task<IActionResult> OnGetAsync([FromQuery(Name = "session_id")] string? sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 255) return RedirectToPage("/Dashboard");

        var result = await workflow.CompletePaymentReturnAsync(UserId, sessionId, ct);
        if (result.Order is null)
        {
            FlashError("We couldn't find that payment. If you were charged, contact support with your receipt.");
            return RedirectToPage("/Receipts/Index");
        }

        if (result.RecordKey is null)
        {
            if (result.Order.Status == OrderStatus.Paid)
                FlashInfo($"Payment received for order {result.Order.OrderNumber}. Your working session has ended — upload the same CSV again and Contact ID {result.Order.ContactId} will be linked to this payment automatically.");
            return RedirectToPage("/Receipts/Details", new { id = result.Order.PublicId });
        }

        return RedirectToPage("/Records/Payment", new { key = result.RecordKey });
    }
}
