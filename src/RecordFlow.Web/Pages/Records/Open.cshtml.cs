using Microsoft.AspNetCore.Mvc;
using RecordFlow.Core;
using RecordFlow.Infrastructure.Services;

namespace RecordFlow.Web.Pages.Records;

/// <summary>Entry point for a record: forwards to whichever step the record is currently on.</summary>
public class OpenModel(RecordWorkflowService workflow) : RecordPageModel(workflow)
{
    public async Task<IActionResult> OnGetAsync(CancellationToken ct) =>
        await LoadAsync(ct) ? RedirectToCurrentStep() : RecordNotFound();

    /// <summary>"Proceed" on the dashboard: builds the form (with billing details) and opens it.</summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return RecordNotFound();
        if (Record.Status != RecordStatus.Imported) return RedirectToCurrentStep();

        await Workflow.GenerateFormAsync(UserId, Key, ct);
        FlashSuccess("Your form is ready. Review it, then share it so the store can complete the missing information and billing details.");
        return RedirectToPage("/Records/Form", new { key = Key });
    }
}
