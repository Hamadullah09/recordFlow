using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RecordFlow.Core.Entities;
using RecordFlow.Infrastructure.Data;

namespace RecordFlow.Infrastructure.Services;

/// <summary>Cached, read-only access to the admin-managed columns and form field definitions.</summary>
public sealed class PortalConfigService(ApplicationDbContext db, IMemoryCache cache)
{
    private const string ColumnsKey = "config:admin-columns";
    private const string FieldsKey = "config:form-fields";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    public async Task<IReadOnlyList<AdminColumn>> GetAdminColumnsAsync(CancellationToken ct = default) =>
        await cache.GetOrCreateAsync(ColumnsKey, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = Ttl;
            return (IReadOnlyList<AdminColumn>)await db.AdminColumns.AsNoTracking().OrderBy(c => c.Slot).ToListAsync(ct);
        }) ?? [];

    /// <summary>Active form field definitions.</summary>
    public async Task<IReadOnlyList<FormFieldDefinition>> GetFormFieldsAsync(CancellationToken ct = default) =>
        await cache.GetOrCreateAsync(FieldsKey, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = Ttl;
            return (IReadOnlyList<FormFieldDefinition>)await db.FormFields.AsNoTracking()
                .Where(f => f.IsActive).OrderBy(f => f.Section).ThenBy(f => f.DisplayOrder).ToListAsync(ct);
        }) ?? [];

    public void Invalidate()
    {
        cache.Remove(ColumnsKey);
        cache.Remove(FieldsKey);
    }
}
