using RecordFlow.Core.Entities;

namespace RecordFlow.Core.Abstractions;

/// <summary>
/// PCI-compliant payment provider abstraction. Providers host the payment page themselves so card
/// numbers and CVCs never reach this application.
/// </summary>
public interface IPaymentProvider
{
    string Name { get; }

    Task<PaymentSession> CreateCheckoutSessionAsync(Order order, PaymentUrls urls, CancellationToken ct = default);

    /// <summary>Authoritative status lookup, used after redirects and webhooks.</summary>
    Task<PaymentStatusResult> GetStatusAsync(string providerSessionId, CancellationToken ct = default);

    /// <summary>Best-effort cancellation of an unpaid session.</summary>
    Task CancelSessionAsync(string providerSessionId, CancellationToken ct = default);
}

public sealed record PaymentUrls(string SuccessUrl, string CancelUrl);

public sealed record PaymentSession(string ProviderSessionId, string RedirectUrl);

public enum ProviderPaymentState { Open, Processing, Paid, Failed, Expired }

public sealed record PaymentStatusResult(
    ProviderPaymentState State,
    string? PaymentReference,
    long? AmountTotalMinor,
    string? CardBrand,
    string? CardLast4,
    string? FailureReason);

public sealed class PaymentOptions
{
    public const string SectionName = "Payments";

    /// <summary>"Stripe" (production) or "Simulated" (development only).</summary>
    public string Provider { get; set; } = "Stripe";
    public bool EnableAch { get; set; }
    public StripeOptions Stripe { get; set; } = new();
}

public sealed class StripeOptions
{
    public string? SecretKey { get; set; }
    public string? WebhookSecret { get; set; }
}
