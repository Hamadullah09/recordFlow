using RecordFlow.Core.Services;

namespace RecordFlow.Core.Abstractions;

public interface IAppEmailSender
{
    Task SendAsync(string toAddress, string subject, string htmlBody, string textBody, CancellationToken ct = default);
}

public interface IAuditLogger
{
    Task LogAsync(
        string category,
        string action,
        string? details = null,
        string? entityType = null,
        string? entityId = null,
        bool succeeded = true,
        string? userId = null,
        string? userName = null,
        CancellationToken ct = default);
}

public interface IAppSettingsService
{
    Task<PricingSettings> GetPricingAsync(CancellationToken ct = default);
    Task SavePricingAsync(PricingSettings settings, string updatedBy, CancellationToken ct = default);
}

public sealed class AppOptions
{
    public const string SectionName = "App";

    public string Name { get; set; } = "RecordFlow";

    /// <summary>
    /// Absolute public URL (e.g. https://portal.example.com). Required in production so that links in
    /// emails and payment redirects are never built from an untrusted Host header.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Windows or IANA time zone used to display dates (default U.S. Eastern).</summary>
    public string TimeZone { get; set; } = "America/New_York";

    public string SupportEmail { get; set; } = "support@example.com";
}

public sealed class WorkspaceOptions
{
    public const string SectionName = "Workspace";

    /// <summary>Workspace is deleted after this much inactivity (user or recipient).</summary>
    public int IdleTimeoutMinutes { get; set; } = 120;

    /// <summary>Absolute lifetime of a workspace and its share links.</summary>
    public int MaxLifetimeHours { get; set; } = 24;

    /// <summary>"Memory" (single server) or "Redis" (multi-server).</summary>
    public string Provider { get; set; } = "Memory";
    public string? RedisConnectionString { get; set; }
}
