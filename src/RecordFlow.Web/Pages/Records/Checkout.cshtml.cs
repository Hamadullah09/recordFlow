using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Core.Services;
using RecordFlow.Core.Workspaces;
using RecordFlow.Infrastructure.Data;
using RecordFlow.Infrastructure.Services;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Pages.Records;

/// <summary>Step 3: the original user enters billing details before payment.</summary>
public class CheckoutModel(
    RecordWorkflowService workflow,
    IAppSettingsService settings,
    IOptions<PaymentOptions> paymentOptions,
    ApplicationDbContext db,
    LinkBuilder links) : RecordPageModel(workflow)
{
    [BindProperty] public CheckoutInput Input { get; set; } = new();

    public PricingSettings Pricing { get; private set; } = new();
    public PriceQuote Quote { get; private set; } = new(0, 0, 0, 0);
    public bool AchEnabled => paymentOptions.Value.EnableAch;
    public IReadOnlyList<string> MissingColumns { get; private set; } = [];
    public IReadOnlyList<KeyValuePair<string, string?>> CsvPreview { get; private set; } = [];
    public int CsvMissingCount { get; private set; }

    public sealed class CheckoutInput
    {
        [Required, StringLength(100), Display(Name = "Full name on billing")]
        public string BillingName { get; set; } = string.Empty;

        [Required, EmailAddress, StringLength(254), Display(Name = "Billing email")]
        public string BillingEmail { get; set; } = string.Empty;

        [Required, StringLength(200), Display(Name = "Street address")]
        public string AddressLine1 { get; set; } = string.Empty;

        [StringLength(200), Display(Name = "Apt, suite, etc. (optional)")]
        public string? AddressLine2 { get; set; }

        [Required, StringLength(100)]
        public string City { get; set; } = string.Empty;

        [Required(ErrorMessage = "Select a state.")]
        public string State { get; set; } = string.Empty;

        [Required, Display(Name = "ZIP code"), RegularExpression(@"^\d{5}(-\d{4})?$", ErrorMessage = "Enter a 5-digit ZIP code (or ZIP+4).")]
        public string ZipCode { get; set; } = string.Empty;

        [Display(Name = "Payment method")]
        public PaymentMethodKind PaymentMethod { get; set; } = PaymentMethodKind.Card;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return RecordNotFound();
        if (Record.IsPaidOrLater) return RedirectToCurrentStep();

        if (Record.Checkout is { } saved)
        {
            Input = new CheckoutInput
            {
                BillingName = saved.BillingName, BillingEmail = saved.BillingEmail,
                AddressLine1 = saved.AddressLine1, AddressLine2 = saved.AddressLine2,
                City = saved.City, State = saved.State, ZipCode = saved.ZipCode, PaymentMethod = saved.PaymentMethod,
            };
        }
        else
        {
            var user = await db.Users.AsNoTracking().Include(u => u.Company).FirstAsync(u => u.Id == UserId, ct);
            Input.BillingName = user.FullName;
            Input.BillingEmail = user.Email ?? string.Empty;
            if (user.Company is { } c)
            {
                Input.AddressLine1 = c.AddressLine1 ?? "";
                Input.AddressLine2 = c.AddressLine2;
                Input.City = c.City ?? "";
                Input.State = c.State ?? "";
                Input.ZipCode = c.ZipCode ?? "";
            }
        }
        await PrepareAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!await LoadAsync(ct)) return RecordNotFound();
        if (Record.IsPaidOrLater) return RedirectToCurrentStep();

        var state = UsStates.Normalize(Input.State);
        if (state is null) ModelState.AddModelError("Input.State", "Select a U.S. state.");
        if (Input.PaymentMethod == PaymentMethodKind.BankAccount && !AchEnabled)
            ModelState.AddModelError("Input.PaymentMethod", "Bank account payments are not available.");
        if (!ModelState.IsValid)
        {
            await PrepareAsync(ct);
            return Page();
        }

        var details = new CheckoutDetails
        {
            BillingName = Input.BillingName.Trim(),
            BillingEmail = Input.BillingEmail.Trim(),
            AddressLine1 = Input.AddressLine1.Trim(),
            AddressLine2 = string.IsNullOrWhiteSpace(Input.AddressLine2) ? null : Input.AddressLine2.Trim(),
            City = Input.City.Trim(),
            State = state!,
            ZipCode = Input.ZipCode.Trim(),
            PaymentMethod = Input.PaymentMethod,
        };

        try
        {
            var redirectUrl = await Workflow.StartCheckoutAsync(UserId, Key, details, order => new PaymentUrls(
                links.Absolute("/checkout/return?session_id={CHECKOUT_SESSION_ID}"),
                links.Absolute($"/checkout/cancel?order={order.PublicId}")), ct);
            return Redirect(redirectUrl);
        }
        catch (WorkflowException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await PrepareAsync(ct);
            return Page();
        }
    }

    private async Task PrepareAsync(CancellationToken ct)
    {
        Pricing = await settings.GetPricingAsync(ct);
        Quote = PricingCalculator.Quote(Pricing);
        MissingColumns = await Workflow.MissingRequiredColumnsAsync(Ctx, ct);
        CsvPreview = Ctx.Workspace.Headers
            .Select(h => new KeyValuePair<string, string?>(h, Record.GetCsvValue(h)))
            .ToList();
        CsvMissingCount = CsvPreview.Count(kv => kv.Value is null);
    }
}
