using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Core.Services;
using Stripe;
using Stripe.Checkout;

namespace RecordFlow.Infrastructure.Payments;

/// <summary>
/// Stripe Checkout integration. Customers enter card details on Stripe's hosted, PCI DSS Level 1
/// payment page, which keeps this application in the lightest PCI scope (SAQ A). Supports Visa,
/// Mastercard, American Express, Discover (and wallets enabled in the Stripe dashboard), plus
/// optional U.S. bank account (ACH) payments.
/// </summary>
public sealed class StripePaymentProvider : IPaymentProvider
{
    private readonly PaymentOptions _options;
    private readonly ILogger<StripePaymentProvider> _logger;
    private readonly Lazy<StripeClient> _client;

    public StripePaymentProvider(IOptions<PaymentOptions> options, ILogger<StripePaymentProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
        _client = new Lazy<StripeClient>(() =>
        {
            if (string.IsNullOrWhiteSpace(_options.Stripe.SecretKey))
                throw new InvalidOperationException("Payments:Stripe:SecretKey is not configured.");
            return new StripeClient(_options.Stripe.SecretKey);
        });
    }

    public string Name => "Stripe";

    public async Task<PaymentSession> CreateCheckoutSessionAsync(Order order, PaymentUrls urls, CancellationToken ct = default)
    {
        var lineItems = new List<SessionLineItemOptions>
        {
            LineItem(order.ServiceName, $"Contact ID {order.ContactId}" + (order.StoreName is null ? "" : $" – {order.StoreName}"), order.Subtotal, order.Currency),
        };
        if (order.Tax > 0) lineItems.Add(LineItem("Sales tax", null, order.Tax, order.Currency));
        if (order.Fee > 0) lineItems.Add(LineItem("Processing fee", null, order.Fee, order.Currency));

        var metadata = new Dictionary<string, string>
        {
            ["order_number"] = order.OrderNumber,
            ["order_id"] = order.PublicId.ToString(),
        };

        var options = new SessionCreateOptions
        {
            Mode = "payment",
            ClientReferenceId = order.OrderNumber,
            CustomerEmail = order.BillingEmail,
            SuccessUrl = urls.SuccessUrl,
            CancelUrl = urls.CancelUrl,
            ExpiresAt = DateTime.UtcNow.AddMinutes(30),
            AllowedPaymentMethodTypes = [order.PaymentMethod == PaymentMethodKind.BankAccount ? "us_bank_account" : "card"],
            LineItems = lineItems,
            Metadata = metadata,
            PaymentIntentData = new SessionPaymentIntentDataOptions
            {
                Description = $"{order.ServiceName} – Order {order.OrderNumber}",
                Metadata = metadata,
            },
        };

        var session = await new SessionService(_client.Value).CreateAsync(
            options,
            new RequestOptions { IdempotencyKey = $"checkout-{order.PublicId}" },
            ct);

        return new PaymentSession(session.Id, session.Url);
    }

    public async Task<PaymentStatusResult> GetStatusAsync(string providerSessionId, CancellationToken ct = default)
    {
        var session = await new SessionService(_client.Value).GetAsync(
            providerSessionId,
            new SessionGetOptions { Expand = ["payment_intent.latest_charge"] },
            cancellationToken: ct);

        var intent = session.PaymentIntent;
        var card = intent?.LatestCharge?.PaymentMethodDetails?.Card;

        var state = session switch
        {
            { PaymentStatus: "paid" or "no_payment_required" } => ProviderPaymentState.Paid,
            { Status: "expired" } => ProviderPaymentState.Expired,
            { Status: "complete" } when intent?.Status is "requires_payment_method" or "canceled" => ProviderPaymentState.Failed,
            { Status: "complete" } => ProviderPaymentState.Processing, // e.g. ACH debit still clearing
            _ => ProviderPaymentState.Open,
        };

        return new PaymentStatusResult(
            state,
            session.PaymentIntentId,
            session.AmountTotal,
            card?.Brand,
            card?.Last4,
            intent?.LastPaymentError?.Message);
    }

    public async Task CancelSessionAsync(string providerSessionId, CancellationToken ct = default)
    {
        try
        {
            await new SessionService(_client.Value).ExpireAsync(providerSessionId, cancellationToken: ct);
        }
        catch (StripeException ex)
        {
            // Already completed or expired – nothing to cancel.
            _logger.LogInformation("Stripe session {SessionId} could not be expired: {Message}", providerSessionId, ex.Message);
        }
    }

    /// <summary>
    /// Verifies a webhook signature and returns the Checkout Session id the event refers to, or null for
    /// events this application does not handle. Throws <see cref="StripeException"/> for invalid signatures.
    /// </summary>
    public string? ParseWebhook(string json, string signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(_options.Stripe.WebhookSecret))
            throw new InvalidOperationException("Payments:Stripe:WebhookSecret is not configured.");

        var stripeEvent = EventUtility.ConstructEvent(json, signatureHeader, _options.Stripe.WebhookSecret, 300, false);
        return stripeEvent.Type switch
        {
            EventTypes.CheckoutSessionCompleted or
            EventTypes.CheckoutSessionAsyncPaymentSucceeded or
            EventTypes.CheckoutSessionAsyncPaymentFailed or
            EventTypes.CheckoutSessionExpired => (stripeEvent.Data.Object as Session)?.Id,
            _ => null,
        };
    }

    private static SessionLineItemOptions LineItem(string name, string? description, decimal amount, string currency) => new()
    {
        Quantity = 1,
        PriceData = new SessionLineItemPriceDataOptions
        {
            Currency = currency,
            UnitAmount = PricingCalculator.ToMinorUnits(amount),
            ProductData = new SessionLineItemPriceDataProductDataOptions { Name = name, Description = description },
        },
    };
}
