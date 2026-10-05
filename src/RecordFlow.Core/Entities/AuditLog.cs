namespace RecordFlow.Core.Entities;

public class AuditLog
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Category { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public bool Succeeded { get; set; } = true;
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    /// <summary>Short description. Never contains CSV contents, passwords or payment data.</summary>
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
}
