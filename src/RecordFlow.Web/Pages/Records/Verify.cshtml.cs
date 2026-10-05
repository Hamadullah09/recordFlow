using Microsoft.AspNetCore.Mvc;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Core.Services;
using RecordFlow.Core.Workspaces;
using RecordFlow.Infrastructure.Services;

namespace RecordFlow.Web.Pages.Records;

/// <summary>Steps 9–10: final review of CSV, user and recipient information, then confirmation.</summary>
public class VerifyModel(RecordWorkflowService workflow, PortalConfigService config) : RecordPageModel(workflow)
{
    [BindProperty] public bool Confirmed { get; set; }

    public IReadOnlyList<string> MissingRequired { get; private set; } = [];
    public IReadOnlyList<(string Label, string Value)> Columns { get; private set; } = [];
    public int RecipientChanges { get; private set; }

    public IEnumerable<IGrouping<FormSection, WorkingField>> Sections =>
        Record.Fields!.OrderBy(f => f.Section).ThenBy(f => f.Order).GroupBy(f => f.Section);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return RecordNotFound();
        if (Record.Fields is null || Record.IsClosed) return RedirectToCurrentStep();
        await PrepareAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return RecordNotFound();
        if (Record.Fields is null || Record.IsClosed) return RedirectToCurrentStep();

        if (!Confirmed)
            ModelState.AddModelError(nameof(Confirmed), "Please confirm that you've reviewed the information.");

        if (ModelState.IsValid)
        {
            try
            {
                var finalized = await Workflow.ConfirmAsync(UserId, Key, DisplayName, ct);
                FlashSuccess($"Record confirmed. Your confirmation number is {finalized.ConfirmationNumber}.");
                return RedirectToPage("/Receipts/Details", new { id = Order!.PublicId });
            }
            catch (WorkflowException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }
        }

        await PrepareAsync(ct);
        return Page();
    }

    private async Task PrepareAsync(CancellationToken ct)
    {
        MissingRequired = Record.Fields!.Where(f => f.IsRequired && f.IsMissing).Select(f => f.Label).ToList();
        RecipientChanges = Record.Fields!.Count(f => f.Source == FieldSource.Recipient);

        var resolver = new AdminColumnResolver(Ctx.Workspace.Headers, await config.GetFormFieldsAsync(ct));
        Columns = resolver.VisibleColumns(await config.GetAdminColumnsAsync(ct), [Record])
            .Select(c => (c.DisplayLabel, resolver.Resolve(c, Record) ?? "—"))
            .ToList();
    }
}
