using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Infrastructure.Data;

namespace RecordFlow.Infrastructure.Auditing;

/// <summary>
/// Writes audit entries to the permanent database through a dedicated context, so an audit write never
/// flushes unrelated pending changes. Audit failures never break the user's request.
/// </summary>
public sealed class AuditLogger(DbContextOptions<ApplicationDbContext> dbOptions, IHttpContextAccessor http, TimeProvider clock, ILogger<AuditLogger> logger) : IAuditLogger
{
    public async Task LogAsync(
        string category, string action, string? details = null, string? entityType = null, string? entityId = null,
        bool succeeded = true, string? userId = null, string? userName = null, CancellationToken ct = default)
    {
        var ctx = http.HttpContext;
        var principal = ctx?.User;
        var entry = new AuditLog
        {
            TimestampUtc = clock.GetUtcNow().UtcDateTime,
            Category = category,
            Action = action,
            Succeeded = succeeded,
            Details = Truncate(details, 2000),
            EntityType = entityType,
            EntityId = Truncate(entityId, 100),
            UserId = userId ?? principal?.FindFirstValue(ClaimTypes.NameIdentifier),
            UserName = Truncate(userName ?? principal?.Identity?.Name, 256),
            IpAddress = ctx?.Connection.RemoteIpAddress?.ToString(),
        };

        try
        {
            await using var db = new ApplicationDbContext(dbOptions);
            db.AuditLogs.Add(entry);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write audit log {Category}/{Action}.", category, action);
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
