using Microsoft.AspNetCore.Mvc;
using RecordFlow.Core;
using RecordFlow.Core.Workspaces;
using RecordFlow.Infrastructure.Services;

namespace RecordFlow.Web.Pages.Records;

/// <summary>Steps 5–6: the generated, editable form and the Share Form modal.</summary>
public class FormModel(RecordWorkflowService workflow) : RecordPageModel(workflow)
{
    [BindProperty] public Dictionary<string, string?> Values { get; set; } = new();
    public IReadOnlyDictionary<string, string> Errors { get; private set; } = new Dictionary<string, string>();

    public IEnumerable<IGrouping<FormSection, WorkingField>> Sections =>
        Record.Fields!.OrderBy(f => f.Section).ThenBy(f => f.Order).GroupBy(f => f.Section);

    /// <summary>Re-displays what the user typed when validation fails.</summary>
    public string? ValueFor(WorkingField f) => Values.TryGetValue(f.Key, out var v) ? v : f.Value;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return RecordNotFound();
        if (Record.Fields is null || Record.IsClosed) return RedirectToCurrentStep();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(string? next, CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return RecordNotFound();
        if (Record.Fields is null || Record.IsClosed) return RedirectToCurrentStep();

        var errors = await Workflow.SaveFormAsync(UserId, Key, Values, DisplayName, ct);
        if (errors.Count > 0)
        {
            Errors = errors;
            TempData["Error"] = $"Please correct the {errors.Count} highlighted field{(errors.Count == 1 ? "" : "s")}.";
            return Page();
        }

        if (next == "verify") return RedirectToPage("/Records/Verify", new { key = Key });
        FlashSuccess("Your changes were saved.");
        return RedirectToPage(new { key = Key });
    }
}
