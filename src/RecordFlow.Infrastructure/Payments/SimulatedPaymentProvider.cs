using Microsoft.Extensions.Caching.Memory;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Core.Security;
using RecordFlow.Core.Services;

namespace RecordFlow.Infrastructure.Payments;

/// <summary>
/// Development-only stand-in for a payment provider so the full workflow can be exercised without
/// Stripe keys. It never asks for or accepts card data. Registration is refused outside Development.
/// </summary>
public sealed class SimulatedPaymentProvider(IMemoryCache cache) : IPaymentProvider
{
    public const string GatewayPath = "/checkout/test-gateway";

    public string Name => "Simulated";

    public Task<PaymentSession> CreateCheckoutSessionAsync(Order order, PaymentUrls urls, CancellationToken ct = default)
    {
        var id = "sim_" + SecureTokens.NewToken(18);
        cache.Set(CacheKey(id), new SimulatedSession
        {
            Id = id,
            OrderNumber = order.OrderNumber,
            AmountMinor = PricingCalculator.ToMinorUnits(order.Total),
            SuccessUrl = urls.SuccessUrl.Replace("{CHECKOUT_SESSION_ID}", id, StringComparison.Ordinal),
            CancelUrl = urls.CancelUrl,
        }, TimeSpan.FromHours(2));

        return Task.FromResult(new PaymentSession(id, $"{GatewayPath}?session={Uri.EscapeDataString(id)}"));
    }

    public Task<PaymentStatusResult> GetStatusAsync(string providerSessionId, CancellationToken ct = default)
    {
        var s = Find(providerSessionId);
        var result = s is null
            ? new PaymentStatusResult(ProviderPaymentState.Expired, null, null, null, null, "Session not found.")
            : new PaymentStatusResult(s.State, s.State == ProviderPaymentState.Paid ? "sim_pi_" + s.Id[4..16] : null,
                s.AmountMinor, s.State == ProviderPaymentState.Paid ? "visa" : null, s.State == ProviderPaymentState.Paid ? "4242" : null,
                s.State == ProviderPaymentState.Failed ? "The test payment was declined." : null);
        return Task.FromResult(result);
    }

    public Task CancelSessionAsync(string providerSessionId, CancellationToken ct = default)
    {
        if (Find(providerSessionId) is { State: ProviderPaymentState.Open } s) s.State = ProviderPaymentState.Expired;
        return Task.CompletedTask;
    }

    public SimulatedSession? Find(string id) => cache.Get<SimulatedSession>(CacheKey(id));

    public SimulatedSession? Complete(string id, bool approve)
    {
        var s = Find(id);
        if (s is { State: ProviderPaymentState.Open })
            s.State = approve ? ProviderPaymentState.Paid : ProviderPaymentState.Failed;
        return s;
    }

    private static string CacheKey(string id) => "simpay:" + id;

    public sealed class SimulatedSession
    {
        public string Id { get; init; } = string.Empty;
        public string OrderNumber { get; init; } = string.Empty;
        public long AmountMinor { get; init; }
        public string SuccessUrl { get; init; } = string.Empty;
        public string CancelUrl { get; init; } = string.Empty;
        public ProviderPaymentState State { get; set; } = ProviderPaymentState.Open;
    }
}
