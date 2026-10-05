using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Core.Services;
using RecordFlow.Infrastructure.Data;

namespace RecordFlow.Infrastructure.Settings;

public sealed class AppSettingsService(ApplicationDbContext db, IMemoryCache cache) : IAppSettingsService
{
    private const string CacheKey = "settings:pricing";
    private const string Prefix = "Pricing.";

    public async Task<PricingSettings> GetPricingAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out PricingSettings? cached) && cached is not null) return Clone(cached);

        var values = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith(Prefix))
            .ToDictionaryAsync(s => s.Key[Prefix.Length..], s => s.Value, ct);

        var defaults = new PricingSettings();
        var settings = new PricingSettings
        {
            ServiceName = values.GetValueOrDefault(nameof(PricingSettings.ServiceName)) ?? defaults.ServiceName,
            ServiceDescription = values.GetValueOrDefault(nameof(PricingSettings.ServiceDescription)) ?? defaults.ServiceDescription,
            UnitPrice = Dec(values, nameof(PricingSettings.UnitPrice), defaults.UnitPrice),
            TaxRatePercent = Dec(values, nameof(PricingSettings.TaxRatePercent), defaults.TaxRatePercent),
            ProcessingFee = Dec(values, nameof(PricingSettings.ProcessingFee), defaults.ProcessingFee),
        };

        cache.Set(CacheKey, settings, TimeSpan.FromMinutes(5));
        return Clone(settings);
    }

    public async Task SavePricingAsync(PricingSettings settings, string updatedBy, CancellationToken ct = default)
    {
        var map = new Dictionary<string, string>
        {
            [nameof(PricingSettings.ServiceName)] = settings.ServiceName,
            [nameof(PricingSettings.ServiceDescription)] = settings.ServiceDescription,
            [nameof(PricingSettings.UnitPrice)] = settings.UnitPrice.ToString(CultureInfo.InvariantCulture),
            [nameof(PricingSettings.TaxRatePercent)] = settings.TaxRatePercent.ToString(CultureInfo.InvariantCulture),
            [nameof(PricingSettings.ProcessingFee)] = settings.ProcessingFee.ToString(CultureInfo.InvariantCulture),
        };

        var keys = map.Keys.Select(k => Prefix + k).ToList();
        var existing = await db.AppSettings.Where(s => keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, ct);
        foreach (var (name, value) in map)
        {
            var key = Prefix + name;
            if (!existing.TryGetValue(key, out var row))
            {
                row = new AppSetting { Key = key };
                db.AppSettings.Add(row);
            }
            row.Value = value;
            row.UpdatedAtUtc = DateTime.UtcNow;
            row.UpdatedBy = updatedBy;
        }
        await db.SaveChangesAsync(ct);
        cache.Remove(CacheKey);
    }

    private static decimal Dec(Dictionary<string, string> values, string key, decimal fallback) =>
        values.TryGetValue(key, out var raw) && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    private static PricingSettings Clone(PricingSettings s) => new()
    {
        ServiceName = s.ServiceName,
        ServiceDescription = s.ServiceDescription,
        UnitPrice = s.UnitPrice,
        TaxRatePercent = s.TaxRatePercent,
        ProcessingFee = s.ProcessingFee,
        Currency = s.Currency,
    };
}
