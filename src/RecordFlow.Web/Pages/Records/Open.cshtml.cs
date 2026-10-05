using Microsoft.AspNetCore.Mvc;
using RecordFlow.Infrastructure.Services;

namespace RecordFlow.Web.Pages.Records;

/// <summary>Entry point for a record: forwards to whichever step the record is currently on.</summary>
public class OpenModel(RecordWorkflowService workflow) : RecordPageModel(workflow)
{
    public async Task<IActionResult> OnGetAsync(CancellationToken ct) =>
        await LoadAsync(ct) ? RedirectToCurrentStep() : RecordNotFound();
}
