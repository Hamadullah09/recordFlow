namespace RecordFlow.Core.Entities;

/// <summary>
/// Permanent record of one call a caller made to a store: when it started and ended, the outcome and the notes.
/// Saved as the call happens, so the history survives the temporary working session and is shown again the next
/// time the same Contact ID is uploaded. Only call metadata is stored – never the uploaded CSV row.
/// </summary>
public class CallLog
{
    public const int MaxNotesLength = 1000;

    public long Id { get; set; }

    /// <summary>Same id as the call in the working session, so updates (end, outcome, notes) find the row.</summary>
    public Guid PublicId { get; set; }

    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    public string ContactId { get; set; } = string.Empty;
    public string? StoreName { get; set; }

    /// <summary>The call list (CSV file name) the call was made from.</summary>
    public string? SourceFile { get; set; }

    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }

    public CallOutcome? Outcome { get; set; }
    public string? Notes { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public TimeSpan? Duration => EndedAtUtc - StartedAtUtc;
}
