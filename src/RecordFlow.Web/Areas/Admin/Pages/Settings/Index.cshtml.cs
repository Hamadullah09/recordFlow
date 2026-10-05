using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Services;
using RecordFlow.Infrastructure.Email;

namespace RecordFlow.Web.Areas.Admin.Pages.Settings;

public class IndexModel(
    IAppSettingsService settings,
    IAuditLogger audit,
    IWorkspaceStore workspaceStore,
    IOptions<CsvImportOptions> csvOptions,
    IOptions<PaymentOptions> paymentOptions,
    IOptions<EmailOptions> emailOptions,
    IOptions<AppOptions> appOptions) : AdminPageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();

    public TimeSpan IdleTimeout => workspaceStore.IdleTimeout;
    public TimeSpan MaxLifetime => workspaceStore.MaxLifetime;
    public CsvImportOptions Csv => csvOptions.Value;
    public PaymentOptions Payments => paymentOptions.Value;
    public EmailOptions Email => emailOptions.Value;
    public AppOptions App => appOptions.Value;
    public PriceQuote Quote { get; private set; } = new(0, 0, 0, 0);

    public sealed class InputModel
    {
        [Required, StringLength(200), Display(Name = "Service name")] public string ServiceName { get; set; } = string.Empty;
        [Required, StringLength(500), Display(Name = "Service description")] public string ServiceDescription { get; set; } = string.Empty;
        [Range(0.50, 100000), Display(Name = "Price per record (USD)")] public decimal UnitPrice { get; set; }
        [Range(0, 25), Display(Name = "Sales tax rate (%)")] public decimal TaxRatePercent { get; set; }
        [Range(0, 1000), Display(Name = "Processing fee (USD)")] public decimal ProcessingFee { get; set; }
    }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var p = await settings.GetPricingAsync(ct);
        Input = new InputModel
        {
            ServiceName = p.ServiceName, ServiceDescription = p.ServiceDescription,
            UnitPrice = p.UnitPrice, TaxRatePercent = p.TaxRatePercent, ProcessingFee = p.ProcessingFee,
        };
        Quote = PricingCalculator.Quote(p);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            Quote = PricingCalculator.Quote(await settings.GetPricingAsync(ct));
            return Page();
        }

        var pricing = new PricingSettings
        {
            ServiceName = Input.ServiceName.Trim(),
            ServiceDescription = Input.ServiceDescription.Trim(),
            UnitPrice = Math.Round(Input.UnitPrice, 2),
            TaxRatePercent = Math.Round(Input.TaxRatePercent, 3),
            ProcessingFee = Math.Round(Input.ProcessingFee, 2),
        };
        await settings.SavePricingAsync(pricing, AdminName, ct);
        var q = PricingCalculator.Quote(pricing);
        await audit.LogAsync(AuditCategories.Admin, "PricingUpdated",
            $"Price {pricing.UnitPrice:C}, tax {pricing.TaxRatePercent}%, fee {pricing.ProcessingFee:C} → total {q.Total:C}", ct: ct);
        FlashSuccess("Pricing saved. New checkouts use the updated amounts.");
        return RedirectToPage();
    }
}
