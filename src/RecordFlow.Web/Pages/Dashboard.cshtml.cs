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
    IOptions<CsvImportOptions> csvOptions) : PortalPageModel
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

    [BindProperty(SupportsGet = true, Name = "q")] public string? Query { get; set; }

    public async Task OnGetAsync(int p = 1, CancellationToken ct = default)
    {
        Workspace = await workflow.GetWorkspaceAsync(UserId, ct);
        if (Workspace is null) return;

        var columns = await config.GetAdminColumnsAsync(ct);
        Resolver = new AdminColumnResolver(Workspace.Headers, await config.GetFormFieldsAsync(ct));
        VisibleColumns = Resolver.VisibleColumns(columns, Workspace.Records);

        TotalCount = Workspace.Records.Count;
        CompletedCount = Workspace.Records.Count(r => r.IsClosed);

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
        PageNumber = Math.Clamp(p, 1, TotalPages);
        Records = list.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList();
    }

    public async Task<IActionResult> OnPostEndSessionAsync(CancellationToken ct)
    {
        await workflow.EndSessionAsync(UserId, "Ended by user", ct);
        FlashSuccess("Your working session has ended and its temporary data was deleted. Completed records and receipts remain available under Orders & Receipts.");
        return RedirectToPage();
    }

    public static string StatusBadgeClass(RecordStatus status) => status switch
    {
        RecordStatus.Imported => "text-bg-light border",
        RecordStatus.CheckoutStarted or RecordStatus.AwaitingPayment => "text-bg-warning",
        RecordStatus.Paid or RecordStatus.FormGenerated => "text-bg-info",
        RecordStatus.SharedPending => "text-bg-primary",
        RecordStatus.ReadyForVerification => "text-bg-purple",
        RecordStatus.Verified => "text-bg-success",
        _ => "text-bg-secondary",
    };

    public static string ActionLabel(WorkingRecord r) => r.Status switch
    {
        RecordStatus.Imported => "Proceed",
        RecordStatus.Verified => "Receipt",
        RecordStatus.ReadyForVerification => "Verify",
        _ => "Continue",
    };
}
