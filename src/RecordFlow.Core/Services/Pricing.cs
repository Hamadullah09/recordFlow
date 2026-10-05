namespace RecordFlow.Core.Services;

public sealed class PricingSettings
{
    public string ServiceName { get; set; } = "Store Contact Verification";
    public string ServiceDescription { get; set; } = "Verification and completion of store, owner, alarm and emergency contact information.";
    public decimal UnitPrice { get; set; } = 49.00m;
    public decimal TaxRatePercent { get; set; }
    public decimal ProcessingFee { get; set; }
    public string Currency { get; set; } = "usd";
}

public sealed record PriceQuote(decimal Subtotal, decimal Tax, decimal Fee, decimal Total);

public static class PricingCalculator
{
    public static PriceQuote Quote(PricingSettings settings)
    {
        var subtotal = Round(settings.UnitPrice);
        var tax = Round(subtotal * settings.TaxRatePercent / 100m);
        var fee = Round(settings.ProcessingFee);
        return new PriceQuote(subtotal, tax, fee, subtotal + tax + fee);
    }

    /// <summary>Amount in the smallest currency unit (cents) as payment providers expect.</summary>
    public static long ToMinorUnits(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
