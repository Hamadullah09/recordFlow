namespace RecordFlow.Core.Entities;

/// <summary>
/// Permanent payment/order record. Contains only non-sensitive checkout information;
/// card numbers and CVCs are handled exclusively by the payment provider.
/// </summary>
public class Order
{
    public int Id { get; set; }

    /// <summary>Opaque identifier used in URLs instead of the database key.</summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public string OrderNumber { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.Created;

    public string ServiceName { get; set; } = string.Empty;
    public string? ServiceDescription { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Fee { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = "usd";

    public string BillingName { get; set; } = string.Empty;
    public string BillingEmail { get; set; } = string.Empty;
    public string BillingAddressLine1 { get; set; } = string.Empty;
    public string? BillingAddressLine2 { get; set; }
    public string BillingCity { get; set; } = string.Empty;
    public string BillingState { get; set; } = string.Empty;
    public string BillingZip { get; set; } = string.Empty;
    public PaymentMethodKind PaymentMethod { get; set; }

    /// <summary>Snapshot of the record this order pays for (no raw CSV data).</summary>
    public string ContactId { get; set; } = string.Empty;
    public string? StoreName { get; set; }

    public string Provider { get; set; } = string.Empty;
    public string? ProviderSessionId { get; set; }
    /// <summary>Provider transaction reference (e.g. Stripe PaymentIntent id).</summary>
    public string? ProviderPaymentId { get; set; }
    public string? CardBrand { get; set; }
    public string? CardLast4 { get; set; }
    public string? FailureReason { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? PaidAtUtc { get; set; }

    public byte[]? RowVersion { get; set; }

    public FinalizedRecord? FinalizedRecord { get; set; }

    public bool IsPaid => Status == OrderStatus.Paid;

    public string BillingCityStateZip => $"{BillingCity}, {BillingState} {BillingZip}";
}
