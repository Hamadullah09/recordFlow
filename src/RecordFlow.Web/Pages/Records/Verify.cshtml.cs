using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Core.Services;
using RecordFlow.Core.Workspaces;
using RecordFlow.Infrastructure.Services;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Pages.Records;

/// <summary>Step 3: final review of CSV, user and recipient information (incl. billing), then confirm → payment.</summary>
public class VerifyModel(
    RecordWorkflowService workflow,
    PortalConfigService config,
    IAppSettingsService settings,
    IOptions<PaymentOptions> paymentOptions,
    LinkBuilder links) : RecordPageModel(workflow)
{
    [BindProperty] public bool Confirmed { get; set; }
    [BindProperty] public PaymentMethodKind PaymentMethod { get; set; } = PaymentMethodKind.Card;

    public IReadOnlyList<string> MissingRequired { get; private set; } = [];
    public IReadOnlyList<(string Label, string Value)> Columns { get; private set; } = [];
    public int RecipientChanges { get; private set; }
    public PriceSummaryModel? Price { get; private set; }
    public bool AchEnabled => paymentOptions.Value.EnableAch;

    /// <summary>Linked to an order that is already paid (e.g. re-uploaded after a lost session): no new charge.</summary>
    public bool AlreadyPaid => Order is { Status: OrderStatus.Paid };

    public IEnumerable<IGrouping<FormSection, WorkingField>> Sections =>
        Record.Fields!.OrderBy(f => f.Section).ThenBy(f => f.Order).GroupBy(f => f.Section);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return RecordNotFound();
        if (Record.Fields is null || Record.IsLocked) return RedirectToCurrentStep();
        PaymentMethod = FormBuilder.ChosenPaymentMethod(Record);   // as chosen on the form (by the recipient)
        await PrepareAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return RecordNotFound();
        if (Record.Fields is null || Record.IsLocked) return RedirectToCurrentStep();

        if (!Confirmed)
            ModelState.AddModelError(nameof(Confirmed), "Please confirm that you've reviewed the information.");
        if (PaymentMethod == PaymentMethodKind.BankAccount && !AchEnabled)
            ModelState.AddModelError(nameof(PaymentMethod), "Bank account payments are not available.");

        if (ModelState.IsValid)
        {
            try
            {
                var result = await Workflow.ConfirmAndPayAsync(UserId, Key, PaymentMethod, DisplayName, order => new PaymentUrls(
                    links.Absolute("/checkout/return?session_id={CHECKOUT_SESSION_ID}"),
                    links.Absolute($"/checkout/cancel?order={order.PublicId}")), ct);
                if (result.PaymentUrl is { } url) return Redirect(url);

                FlashSuccess($"Record confirmed. Your confirmation number is {result.Finalized?.ConfirmationNumber}.");
                return RedirectToPage("/Receipts/Details", new { id = result.Order.PublicId });
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

        var pricing = await settings.GetPricingAsync(ct);
        Price = new PriceSummaryModel(pricing, PricingCalculator.Quote(pricing),
            "You'll enter the card on our payment processor's secure page. We never see or store card numbers.");
    }
}
