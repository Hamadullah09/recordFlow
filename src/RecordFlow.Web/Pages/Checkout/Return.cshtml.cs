using Microsoft.AspNetCore.Mvc;
using RecordFlow.Core;
using RecordFlow.Infrastructure.Services;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Pages.Checkout;

/// <summary>
/// Where the payment provider sends the customer back. The query string is never trusted: the order is
/// looked up for the signed-in user and its status is re-verified with the provider. A paid order completes
/// the confirmed record and shows the receipt.
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

        var order = result.Order;
        if (order.Status == OrderStatus.Paid)
        {
            if (result.RecordKey is null)
                FlashInfo($"Payment received for order {order.OrderNumber}. Your working session has ended — upload the same CSV again and confirm Contact ID {order.ContactId}; you won't be charged twice.");
            else
                FlashSuccess($"Payment received. The record for Contact ID {order.ContactId} is complete.");
            return RedirectToPage("/Receipts/Details", new { id = order.PublicId });
        }

        if (order.Status is OrderStatus.Failed or OrderStatus.Canceled)
            FlashError($"The payment was not completed{(order.FailureReason is null ? "" : $": {order.FailureReason.TrimEnd('.')}")}. You have not been charged — review the details and try again.");

        return result.RecordKey is null
            ? RedirectToPage("/Receipts/Details", new { id = order.PublicId })
            : RedirectToPage("/Records/Open", new { key = result.RecordKey });
    }
}
