using Microsoft.AspNetCore.Mvc;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Services;
using RecordFlow.Core.Workspaces;
using RecordFlow.Infrastructure.Services;

namespace RecordFlow.Web.Pages.Records;

/// <summary>Step 2: the generated, editable form (store and billing details) and the Share Form modal.</summary>
public class FormModel(RecordWorkflowService workflow, IAppSettingsService settings) : RecordPageModel(workflow)
{
    [BindProperty] public Dictionary<string, string?> Values { get; set; } = new();
    public IReadOnlyDictionary<string, string> Errors { get; private set; } = new Dictionary<string, string>();
    public PriceSummaryModel? Price { get; private set; }

    public IEnumerable<IGrouping<FormSection, WorkingField>> Sections =>
        Record.Fields!.OrderBy(f => f.Section).ThenBy(f => f.Order).GroupBy(f => f.Section);

    /// <summary>Re-displays what the user typed when validation fails.</summary>
    public string? ValueFor(WorkingField f) => Values.TryGetValue(f.Key, out var v) ? v : f.Value;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return RecordNotFound();
        if (Record.Fields is null || Record.IsLocked) return RedirectToCurrentStep();
        await PrepareAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(string? next, CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return RecordNotFound();
        if (Record.Fields is null || Record.IsLocked) return RedirectToCurrentStep();

        var errors = await Workflow.SaveFormAsync(UserId, Key, Values, DisplayName, ct);
        if (errors.Count > 0)
        {
            Errors = errors;
            TempData["Error"] = $"Please correct the {errors.Count} highlighted field{(errors.Count == 1 ? "" : "s")}.";
            await PrepareAsync(ct);
            return Page();
        }

        if (next == "verify") return RedirectToPage("/Records/Verify", new { key = Key });
        FlashSuccess("Your changes were saved.");
        return RedirectToPage(new { key = Key });
    }

    private async Task PrepareAsync(CancellationToken ct)
    {
        var pricing = await settings.GetPricingAsync(ct);
        Price = new PriceSummaryModel(pricing, PricingCalculator.Quote(pricing),
            "Charged after you verify and confirm the record. Card details are entered only on the secure payment page.");
    }
}
