namespace RecordFlow.Core.Workspaces;

/// <summary>
/// A user's temporary working session created from one uploaded CSV. Workspaces live only in the
/// encrypted, expiring server-side cache – never in the permanent database – and are owned by
/// exactly one user.
/// </summary>
public sealed class Workspace
{
    public string Id { get; set; } = string.Empty;
    public string OwnerUserId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Hard limit regardless of activity.</summary>
    public DateTimeOffset AbsoluteExpiresAtUtc { get; set; }

    /// <summary>CSV headers in file order (after de-duplication).</summary>
    public List<string> Headers { get; set; } = [];

    /// <summary>Index of the header that supplied Contact ID, or -1 when IDs were generated.</summary>
    public int ContactIdHeaderIndex { get; set; } = -1;

    public List<string> Warnings { get; set; } = [];
    public List<WorkingRecord> Records { get; set; } = [];

    public WorkingRecord? FindRecord(string? key) =>
        string.IsNullOrEmpty(key) ? null : Records.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.Ordinal));

    public WorkingRecord? FindRecordByOrder(Guid orderPublicId) =>
        Records.FirstOrDefault(r => r.OrderPublicId == orderPublicId);

    public string? ContactIdHeader =>
        ContactIdHeaderIndex >= 0 && ContactIdHeaderIndex < Headers.Count ? Headers[ContactIdHeaderIndex] : null;

    public bool AllRecordsClosed => Records.Count > 0 && Records.All(r => r.Status == RecordStatus.Verified);
}
