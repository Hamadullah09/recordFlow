using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Core.Services;
using RecordFlow.Core.Workspaces;
using RecordFlow.Infrastructure.Services;
using RecordFlow.Web.Infrastructure;

namespace RecordFlow.Web.Pages;

public class DashboardModel(
    RecordWorkflowService workflow,
    PortalConfigService config,
    IWorkspaceStore store,
    IOptions<CsvImportOptions> csvOptions,
    TimeDisplay time,
    TimeProvider clock) : PortalPageModel
{
    private const int PageSize = 25;

    public Workspace? Workspace { get; private set; }
    public IReadOnlyList<AdminColumn> VisibleColumns { get; private set; } = [];
    public AdminColumnResolver? Resolver { get; private set; }
    public IReadOnlyList<WorkingRecord> Records { get; private set; } = [];
    public int TotalCount { get; private set; }
    public int FilteredCount { get; private set; }
    public int CompletedCount { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public int TotalPages { get; private set; } = 1;
    public long MaxUploadMb => Math.Max(1, csvOptions.Value.MaxFileBytes / (1024 * 1024));
    public long MaxUploadBytes => csvOptions.Value.MaxFileBytes;
    public TimeSpan IdleTimeout => store.IdleTimeout;

    // Caller stats for the current working session.
    public int CallsToday { get; private set; }
    public TimeSpan? AverageCall { get; private set; }
    public int NotCalledCount { get; private set; }
    public int InterestedCount { get; private set; }
    public int FollowUpCount { get; private set; }
    public WorkingRecord? ActiveCall { get; private set; }

    /// <summary>The next store to call (never called first, then follow-ups) and its page.</summary>
    public WorkingRecord? NextRecord { get; private set; }
    public int NextRecordPage { get; private set; } = 1;

    [BindProperty(SupportsGet = true, Name = "q")] public string? Query { get; set; }

    /// <summary>e.g. "Tuesday, Oct 6" in the portal time zone.</summary>
    public string TodayLabel => time.ToLocal(clock.GetUtcNow()).ToString("dddd, MMM d", System.Globalization.CultureInfo.InvariantCulture);

    /// <param name="p">Page of the call queue.</param>
    /// <param name="call">Key of a store to open; the queue jumps to the page that holds it.</param>
    public async Task OnGetAsync(int p = 1, string? call = null, CancellationToken ct = default)
    {
        await workflow.RefreshPaymentsAsync(UserId, ct);
        Workspace = await workflow.GetWorkspaceAsync(UserId, ct);
        if (Workspace is null) return;

        var columns = await config.GetAdminColumnsAsync(ct);
        Resolver = new AdminColumnResolver(Workspace.Headers, await config.GetFormFieldsAsync(ct));
        VisibleColumns = Resolver.VisibleColumns(columns, Workspace.Records);

        TotalCount = Workspace.Records.Count;
        CompletedCount = Workspace.Records.Count(r => r.IsClosed);

        var today = time.ToLocal(clock.GetUtcNow()).Date;
        var calls = Workspace.Records.SelectMany(r => r.Calls).ToList();
        CallsToday = calls.Count(c => time.ToLocal(c.StartedAtUtc).Date == today);
        var durations = calls.Where(c => c.Duration is not null).Select(c => c.Duration!.Value.Ticks).ToList();
        AverageCall = durations.Count == 0 ? null : TimeSpan.FromTicks((long)durations.Average());
        NotCalledCount = Workspace.Records.Count(r => r.Calls.Count == 0 && !r.IsClosed);
        ActiveCall = Workspace.Records.FirstOrDefault(r => r.IsOnCall);
        InterestedCount = Workspace.Records.Count(r => r.LastCall?.Outcome == CallOutcome.Interested);
        FollowUpCount = Workspace.Records.Count(r => !r.IsClosed && r.LastCall is { NeedsFollowUp: true });

        // Next call: stores never called first, then stores waiting for a call back / no answer / voicemail.
        var nextIndex = Workspace.Records.FindIndex(r => r.Calls.Count == 0 && !r.IsClosed);
        if (nextIndex < 0)
            nextIndex = Workspace.Records.FindIndex(r => !r.IsClosed && !r.IsOnCall && r.LastCall is { NeedsFollowUp: true });
        if (nextIndex >= 0)
        {
            NextRecord = Workspace.Records[nextIndex];
            NextRecordPage = nextIndex / PageSize + 1;
        }

        IEnumerable<WorkingRecord> filtered = Workspace.Records;
        if (!string.IsNullOrWhiteSpace(Query))
        {
            var q = Query.Trim();
            filtered = filtered.Where(r =>
                r.ContactId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (r.StoreName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                VisibleColumns.Any(c => Resolver.Resolve(c, r)?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        var list = filtered.ToList();
        FilteredCount = list.Count;
        TotalPages = Math.Max(1, (int)Math.Ceiling(FilteredCount / (double)PageSize));
        if (!string.IsNullOrEmpty(call))
        {
            var callIndex = list.FindIndex(r => r.Key == call);
            if (callIndex >= 0) p = callIndex / PageSize + 1;
        }
        PageNumber = Math.Clamp(p, 1, TotalPages);
        Records = list.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList();
    }

    public async Task<IActionResult> OnPostEndSessionAsync(CancellationToken ct)
    {
        await workflow.EndSessionAsync(UserId, "Ended by user", ct);
        FlashSuccess("Your working session has ended and its temporary data was deleted. Completed records and receipts remain available under Orders & Receipts.");
        return RedirectToPage();
    }

    /// <summary>The number to dial for a record (store phone first), for click-to-call.</summary>
    public PhoneLink? PhoneFor(WorkingRecord r) => Workspace is null ? null : PhoneNumbers.Primary(r, Workspace.Headers);

    public static string OutcomeBadgeClass(CallOutcome outcome) => outcome switch
    {
        CallOutcome.Interested => "text-bg-success",
        CallOutcome.CallBack => "text-bg-warning",
        CallOutcome.NotInterested => "text-bg-secondary",
        CallOutcome.WrongNumber => "text-bg-danger",
        _ => "text-bg-light border",
    };

    public static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d:mm\\:ss}" : d.ToString(@"mm\:ss");

    public static string StatusBadgeClass(RecordStatus status) => status switch
    {
        RecordStatus.Imported => "text-bg-light border",
        RecordStatus.FormGenerated => "text-bg-info",
        RecordStatus.SharedPending => "text-bg-primary",
        RecordStatus.ReadyForVerification => "text-bg-purple",
        RecordStatus.AwaitingPayment => "text-bg-warning",
        RecordStatus.Verified => "text-bg-success",
        _ => "text-bg-secondary",
    };

    public static string ActionLabel(WorkingRecord r) => r.Status switch
    {
        RecordStatus.Imported => "Proceed",
        RecordStatus.Verified => "Receipt",
        RecordStatus.ReadyForVerification => "Verify",
        RecordStatus.AwaitingPayment => "Pay",
        _ => "Continue",
    };
}
