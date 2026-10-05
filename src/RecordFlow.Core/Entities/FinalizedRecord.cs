using System.Text.Json;

namespace RecordFlow.Core.Entities;

/// <summary>
/// The only record-level data that is retained after a workflow completes: the confirmed
/// final values. The uploaded CSV itself and the original CSV cell values are never stored.
/// </summary>
public class FinalizedRecord
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public string ConfirmationNumber { get; set; } = string.Empty;

    public int OrderId { get; set; }
    public Order? Order { get; set; }

    public string UserId { get; set; } = string.Empty;
    public string? CompanyName { get; set; }

    public string ContactId { get; set; } = string.Empty;
    public string? StoreName { get; set; }
    /// <summary>"Y" or "N" as selected on the dashboard, if any.</summary>
    public string? Response { get; set; }

    /// <summary>JSON array of <see cref="FinalizedField"/>.</summary>
    public string FieldsJson { get; set; } = "[]";

    /// <summary>JSON array of <see cref="FinalizedField"/> for the admin columns at close time.</summary>
    public string AdminColumnsJson { get; set; } = "[]";

    public DateTime RecordCreatedAtUtc { get; set; }
    public DateTime? RecipientSubmittedAtUtc { get; set; }
    public DateTime ConfirmedAtUtc { get; set; }
    public string ConfirmedByUserId { get; set; } = string.Empty;
    public string ConfirmedByName { get; set; } = string.Empty;
    public DateTime ClosedAtUtc { get; set; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<FinalizedField> GetFields() =>
        JsonSerializer.Deserialize<List<FinalizedField>>(FieldsJson, Json) ?? [];

    public IReadOnlyList<FinalizedField> GetAdminColumns() =>
        JsonSerializer.Deserialize<List<FinalizedField>>(AdminColumnsJson, Json) ?? [];

    public void SetFields(IEnumerable<FinalizedField> fields) =>
        FieldsJson = JsonSerializer.Serialize(fields, Json);

    public void SetAdminColumns(IEnumerable<FinalizedField> columns) =>
        AdminColumnsJson = JsonSerializer.Serialize(columns, Json);
}

public sealed record FinalizedField(string Section, string Label, string? Value, string Source);
