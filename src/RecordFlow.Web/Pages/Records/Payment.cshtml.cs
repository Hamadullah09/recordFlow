using Microsoft.AspNetCore.Mvc;
using RecordFlow.Core;
using RecordFlow.Infrastructure.Services;

namespace RecordFlow.Web.Pages.Records;

/// <summary>Step 4/5: payment result, then "Generate Form".</summary>
public class PaymentModel(RecordWorkflowService workflow) : RecordPageModel(workflow)
{
    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return RecordNotFound();
        if (Order is null || Record.Status is RecordStatus.Imported or RecordStatus.CheckoutStarted)
            return RedirectToPage("/Records/Checkout", new { key = Key });
        if (Record.Status >= RecordStatus.FormGenerated) return RedirectToCurrentStep();
        return Page();
    }

    public async Task<IActionResult> OnPostGenerateAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return RecordNotFound();
        await Workflow.GenerateFormAsync(UserId, Key, ct);
        FlashSuccess("Your form is ready. Review it, then share it with the person who can complete the missing information.");
        return RedirectToPage("/Records/Form", new { key = Key });
    }
}
