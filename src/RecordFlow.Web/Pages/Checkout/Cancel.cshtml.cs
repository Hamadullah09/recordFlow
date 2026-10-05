using Microsoft.AspNetCore.Mvc;
using RecordFlow.Core;
using RecordFlow.Infrastructure.Services;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Pages.Checkout;

public class CancelModel(RecordWorkflowService workflow) : PortalPageModel
{
    public async Task<IActionResult> OnGetAsync([FromQuery] Guid order, CancellationToken ct)
    {
        var result = await workflow.CancelPaymentAsync(UserId, order, ct);
        if (result.Order is null) return RedirectToPage("/Dashboard");

        if (result.Order.Status == OrderStatus.Paid)
        {
            // The payment completed before the cancel arrived – continue normally.
            return result.RecordKey is null
                ? RedirectToPage("/Receipts/Details", new { id = result.Order.PublicId })
                : RedirectToPage("/Records/Payment", new { key = result.RecordKey });
        }

        FlashInfo("Payment was canceled and you were not charged. You can review your details and try again.");
        return result.RecordKey is null
            ? RedirectToPage("/Dashboard")
            : RedirectToPage("/Records/Checkout", new { key = result.RecordKey });
    }
}
