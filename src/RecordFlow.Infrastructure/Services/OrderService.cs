using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Core.Security;
using RecordFlow.Core.Services;
using RecordFlow.Core.Workspaces;
using RecordFlow.Infrastructure.Data;

namespace RecordFlow.Infrastructure.Services;

/// <summary>
/// Creates orders and keeps their status in sync with the payment provider. The provider is always
/// treated as the source of truth: redirects and webhooks only trigger a fresh status lookup.
/// </summary>
public sealed class OrderService(
    ApplicationDbContext db,
    IPaymentProvider provider,
    IAuditLogger audit,
    TimeProvider clock,
    ILogger<OrderService> logger)
{
    public Task<Order?> GetForUserAsync(string userId, Guid publicId, CancellationToken ct = default) =>
        db.Orders.FirstOrDefaultAsync(o => o.PublicId == publicId && o.UserId == userId, ct);

    public Task<Order?> GetBySessionForUserAsync(string userId, string sessionId, CancellationToken ct = default) =>
        db.Orders.FirstOrDefaultAsync(o => o.ProviderSessionId == sessionId && o.UserId == userId, ct);

    public async Task<(Order Order, string RedirectUrl)> StartCheckoutAsync(
        string userId,
        CheckoutDetails details,
        string contactId,
        string? storeName,
        PricingSettings pricing,
        Func<Order, PaymentUrls> buildUrls,
        CancellationToken ct = default)
    {
        var quote = PricingCalculator.Quote(pricing);
        if (quote.Total < 0.50m)
            throw new WorkflowException("The service price has not been configured yet. Please contact support.");

        var now = clock.GetUtcNow().UtcDateTime;
        var order = new Order
        {
            OrderNumber = SecureTokens.NewOrderNumber(now),
            UserId = userId,
            Status = OrderStatus.Pending,
            ServiceName = pricing.ServiceName,
            ServiceDescription = pricing.ServiceDescription,
            Subtotal = quote.Subtotal,
            Tax = quote.Tax,
            Fee = quote.Fee,
            Total = quote.Total,
            Currency = pricing.Currency,
            BillingName = details.BillingName,
            BillingEmail = details.BillingEmail,
            BillingAddressLine1 = details.AddressLine1,
            BillingAddressLine2 = details.AddressLine2,
            BillingCity = details.City,
            BillingState = details.State,
            BillingZip = details.ZipCode,
            PaymentMethod = details.PaymentMethod,
            ContactId = contactId,
            StoreName = storeName is { Length: > 250 } ? storeName[..250] : storeName,
            Provider = provider.Name,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);

        try
        {
            var session = await provider.CreateCheckoutSessionAsync(order, buildUrls(order), ct);
            order.ProviderSessionId = session.ProviderSessionId;
            order.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(AuditCategories.Payment, "CheckoutStarted",
                $"{order.Total:C} via {provider.Name} ({order.PaymentMethod})", nameof(Order), order.OrderNumber, ct: ct);
            return (order, session.RedirectUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not create a payment session for order {OrderNumber}.", order.OrderNumber);
            order.Status = OrderStatus.Failed;
            order.FailureReason = "The payment session could not be started.";
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(AuditCategories.Payment, "CheckoutStartFailed", ex.GetType().Name, nameof(Order), order.OrderNumber, succeeded: false, ct: ct);
            throw new WorkflowException("We couldn't reach the payment processor. Please try again in a moment.");
        }
    }

    /// <summary>Refreshes an order from the provider while it is still pending/processing.</summary>
    public async Task<Order> SyncAsync(Order order, CancellationToken ct = default)
    {
        if (order.Status is not (OrderStatus.Pending or OrderStatus.Processing) || order.ProviderSessionId is null)
            return order;

        PaymentStatusResult status;
        try
        {
            status = await provider.GetStatusAsync(order.ProviderSessionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Payment status lookup failed for order {OrderNumber}.", order.OrderNumber);
            return order;
        }

        var previous = order.Status;
        var now = clock.GetUtcNow().UtcDateTime;
        switch (status.State)
        {
            case ProviderPaymentState.Paid:
                var expected = PricingCalculator.ToMinorUnits(order.Total);
                if (status.AmountTotalMinor is long paid && paid != expected)
                {
                    order.Status = OrderStatus.Failed;
                    order.FailureReason = $"Amount mismatch: provider reported {paid} minor units, expected {expected}.";
                    logger.LogCritical("Payment amount mismatch on order {OrderNumber}: {Paid} vs {Expected}.", order.OrderNumber, paid, expected);
                }
                else
                {
                    order.Status = OrderStatus.Paid;
                    order.PaidAtUtc ??= now;
                    order.ProviderPaymentId = status.PaymentReference;
                    order.CardBrand = status.CardBrand;
                    order.CardLast4 = status.CardLast4;
                    order.FailureReason = null;
                }
                break;
            case ProviderPaymentState.Processing:
                order.Status = OrderStatus.Processing;
                break;
            case ProviderPaymentState.Failed:
                order.Status = OrderStatus.Failed;
                order.FailureReason = status.FailureReason ?? "The payment was declined.";
                break;
            case ProviderPaymentState.Expired:
                order.Status = OrderStatus.Canceled;
                order.FailureReason ??= "The payment session expired before payment was completed.";
                break;
        }

        if (order.Status == previous) return order;

        order.UpdatedAtUtc = now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A webhook and a browser redirect raced; the other request already stored the update.
            await db.Entry(order).ReloadAsync(ct);
            return order;
        }

        await audit.LogAsync(AuditCategories.Payment, $"Payment{order.Status}",
            $"{previous} → {order.Status}" + (order.FailureReason is null ? "" : $": {order.FailureReason}"),
            nameof(Order), order.OrderNumber, succeeded: order.Status is OrderStatus.Paid or OrderStatus.Processing,
            userId: order.UserId, ct: ct);
        return order;
    }

    /// <summary>Webhook entry point: re-verifies the session with the provider.</summary>
    public async Task<Order?> SyncBySessionIdAsync(string sessionId, CancellationToken ct = default)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.ProviderSessionId == sessionId, ct);
        return order is null ? null : await SyncAsync(order, ct);
    }

    public async Task CancelAsync(Order order, CancellationToken ct = default)
    {
        if (order.Status != OrderStatus.Pending) return;
        if (order.ProviderSessionId is not null)
        {
            await provider.CancelSessionAsync(order.ProviderSessionId, ct);
            await SyncAsync(order, ct);
        }
        if (order.Status == OrderStatus.Pending)
        {
            order.Status = OrderStatus.Canceled;
            order.FailureReason = "Canceled by the customer.";
            order.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(AuditCategories.Payment, "PaymentCanceled", null, nameof(Order), order.OrderNumber, ct: ct);
        }
    }
}
