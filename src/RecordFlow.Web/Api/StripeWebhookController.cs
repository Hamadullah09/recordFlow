using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RecordFlow.Infrastructure.Payments;
using RecordFlow.Infrastructure.Services;
using Stripe;

namespace RecordFlow.Web.Api;

/// <summary>
/// Stripe webhook endpoint. The signature is verified with the webhook signing secret, and the event only
/// triggers a fresh, authenticated status lookup – the event payload itself is never trusted for amounts.
/// </summary>
[ApiController]
[Route("api/payments/stripe/webhook")]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public class StripeWebhookController(IServiceProvider services, OrderService orders, ILogger<StripeWebhookController> logger) : ControllerBase
{
    private const long MaxPayloadBytes = 512 * 1024;

    [HttpPost]
    [RequestSizeLimit(MaxPayloadBytes)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        var stripe = services.GetService<StripePaymentProvider>();
        if (stripe is null) return NotFound();

        string json;
        using (var reader = new StreamReader(Request.Body))
            json = await reader.ReadToEndAsync(ct);

        string? sessionId;
        try
        {
            sessionId = stripe.ParseWebhook(json, Request.Headers["Stripe-Signature"].ToString());
        }
        catch (StripeException ex)
        {
            logger.LogWarning("Rejected Stripe webhook: {Message}", ex.Message);
            return BadRequest();
        }

        if (sessionId is not null)
        {
            var order = await orders.SyncBySessionIdAsync(sessionId, ct);
            logger.LogInformation("Stripe webhook processed for session {SessionId}; order status {Status}.", sessionId, order?.Status);
        }
        return Ok();
    }
}
