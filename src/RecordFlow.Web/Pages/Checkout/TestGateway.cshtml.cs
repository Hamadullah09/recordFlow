using Microsoft.AspNetCore.Mvc;
using RecordFlow.Infrastructure.Payments;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Pages.Checkout;

/// <summary>
/// Development-only stand-in for the provider's hosted payment page. It never collects card data and
/// returns 404 unless the app runs in Development with the Simulated provider.
/// </summary>
public class TestGatewayModel(IWebHostEnvironment env, IServiceProvider services) : PortalPageModel
{
    [BindProperty(SupportsGet = true, Name = "session")] public string? SessionId { get; set; }
    public SimulatedPaymentProvider.SimulatedSession? Session { get; private set; }

    public IActionResult OnGet()
    {
        var provider = Provider();
        if (provider is null || SessionId is null) return NotFound();
        Session = provider.Find(SessionId);
        return Session is null ? NotFound() : Page();
    }

    public IActionResult OnPost(string decision)
    {
        var provider = Provider();
        if (provider is null || SessionId is null) return NotFound();
        var session = provider.Find(SessionId);
        if (session is null) return NotFound();

        if (decision == "cancel") return LocalRedirectOrAbsolute(session.CancelUrl);
        provider.Complete(SessionId, approve: decision == "approve");
        return LocalRedirectOrAbsolute(session.SuccessUrl);
    }

    private SimulatedPaymentProvider? Provider() =>
        env.IsDevelopment() ? services.GetService<SimulatedPaymentProvider>() : null;

    private IActionResult LocalRedirectOrAbsolute(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var abs) && abs.Host == Request.Host.Host
            ? Redirect(abs.PathAndQuery)
            : LocalRedirect(url);
}
