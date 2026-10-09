using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Core.Security;
using RecordFlow.Core.Services;
using RecordFlow.Core.Workspaces;
using RecordFlow.Infrastructure.Data;
using RecordFlow.Infrastructure.Email;

namespace RecordFlow.Infrastructure.Services;

public sealed record RecordContext(Workspace Workspace, WorkingRecord Record, Order? Order);

public sealed record SharedFormContext(Workspace Workspace, WorkingRecord Record, Order? Order, string RequestedBy, string? CompanyName);

public sealed record ShareLink(string Token, DateTimeOffset ExpiresAtUtc, DateTimeOffset? SubmittedAtUtc, DateTimeOffset? LastOpenedAtUtc);

public sealed record PaymentReturn(Order? Order, string? RecordKey);

/// <summary>Result of "Confirm &amp; pay": either a payment page to redirect to, or the record was already paid and is now finalized.</summary>
public sealed record ConfirmResult(Order Order, string? PaymentUrl, FinalizedRecord? Finalized);

public enum RecipientSubmitOutcome { Submitted, ValidationFailed, LinkInvalid }

public sealed record RecipientSubmitResult(RecipientSubmitOutcome Outcome, IReadOnlyDictionary<string, string> Errors);

/// <summary>
/// Orchestrates the end-to-end workflow:
/// Upload CSV → select record (call timer) → generate form (incl. billing details) → share → recipient
/// completes/edits → automatic update → verify → confirm → payment → receipt.
/// Temporary data lives in <see cref="IWorkspaceStore"/>; only orders, finalized records and audit
/// entries are written to the permanent database.
/// </summary>
public sealed class RecordWorkflowService(
    IWorkspaceStore store,
    ICsvImportService csvImport,
    ApplicationDbContext db,
    OrderService orders,
    PortalConfigService config,
    IAppSettingsService settings,
    IAuditLogger audit,
    IAppEmailSender email,
    IOptions<AppOptions> appOptions,
    IOptions<PaymentOptions> paymentOptions,
    TimeProvider clock,
    ILogger<RecordWorkflowService> logger)
{
    // ───────────────────────── Workspace ─────────────────────────

    public Task<Workspace?> GetWorkspaceAsync(string userId, CancellationToken ct = default) =>
        store.GetForUserAsync(userId, ct);

    public async Task<CsvImportResult> ImportCsvAsync(string userId, Stream stream, string fileName, CancellationToken ct = default)
    {
        var safeName = Path.GetFileName(fileName);
        if (safeName.Length > 100) safeName = safeName[..100];

        var result = await csvImport.ImportAsync(stream, safeName, ct);
        if (!result.Succeeded)
        {
            await audit.LogAsync(AuditCategories.Workspace, "CsvRejected", $"{safeName}: {result.Errors[0]}", succeeded: false, ct: ct);
            return result;
        }

        var now = clock.GetUtcNow();
        var workspace = new Workspace
        {
            Id = SecureTokens.NewToken(18),
            OwnerUserId = userId,
            FileName = safeName,
            CreatedAtUtc = now,
            AbsoluteExpiresAtUtc = now + store.MaxLifetime,
            Headers = result.Headers,
            ContactIdHeaderIndex = result.ContactIdHeaderIndex,
            Warnings = result.Warnings,
            Records = result.Records,
        };
        foreach (var r in workspace.Records) r.StoreName = FormBuilder.FindStoreName(r, workspace.Headers);
        await CloseOpenCallsAsync(userId, ct);           // the new upload replaces the previous working session
        await LoadCallHistoryAsync(userId, workspace, ct);
        var relinked = await RelinkPaidOrdersAsync(userId, workspace, ct);
        if (relinked > 0)
            result.Warnings.Add($"{relinked} record(s) were matched to orders you already paid for. They won't be charged again when you confirm them.");

        await store.SaveNewAsync(workspace, ct);
        // Only metadata is logged – never CSV contents.
        await audit.LogAsync(AuditCategories.Workspace, "CsvImported",
            $"{safeName}: {result.Records.Count} record(s), {result.Headers.Count} column(s)", ct: ct);
        return result;
    }

    /// <summary>
    /// If a previous session ended after payment but before the record was finalized, re-uploading the CSV
    /// re-attaches the paid order to the matching Contact ID so the customer never pays twice: confirming the
    /// record again finalizes it against that order without a new payment.
    /// </summary>
    private async Task<int> RelinkPaidOrdersAsync(string userId, Workspace workspace, CancellationToken ct)
    {
        var openOrders = await db.Orders.AsNoTracking()
            .Where(o => o.UserId == userId && o.Status == OrderStatus.Paid && o.FinalizedRecord == null)
            .OrderBy(o => o.CreatedAtUtc)
            .Select(o => new { o.PublicId, o.OrderNumber, o.ContactId })
            .ToListAsync(ct);

        var linked = 0;
        foreach (var order in openOrders)
        {
            var record = workspace.Records.FirstOrDefault(r =>
                !r.ContactIdGenerated && r.OrderPublicId is null &&
                string.Equals(r.ContactId, order.ContactId, StringComparison.OrdinalIgnoreCase));
            if (record is null) continue;
            record.OrderPublicId = order.PublicId;
            record.OrderNumber = order.OrderNumber;
            linked++;
        }
        return linked;
    }

    public async Task EndSessionAsync(string userId, string reason, CancellationToken ct = default)
    {
        await store.DeleteForUserAsync(userId, ct);
        await CloseOpenCallsAsync(userId, ct);
        await audit.LogAsync(AuditCategories.Workspace, "WorkspaceEnded", reason, userId: userId, ct: ct);
    }

    /// <summary>
    /// Loads a record from the caller's own workspace (the only place it can be found) and reconciles
    /// its status with the order in the database. Returns null when the key is not in the workspace.
    /// </summary>
    public async Task<RecordContext?> LoadRecordAsync(string userId, string key, CancellationToken ct = default)
    {
        var ws = await store.GetForUserAsync(userId, ct) ?? throw new WorkspaceExpiredException();
        var record = ws.FindRecord(key);
        if (record is null) return null;

        Order? order = null;
        if (record.OrderPublicId is Guid orderId)
        {
            order = await orders.GetForUserAsync(userId, orderId, ct);
            if (order is { Status: OrderStatus.Pending or OrderStatus.Processing })
                order = await orders.SyncAsync(order, ct);

            if (order is not null && record.Status == RecordStatus.AwaitingPayment)
            {
                if (order.Status == OrderStatus.Paid)
                {
                    // Payment confirmed (by the redirect, a webhook or this lookup): the record is completed.
                    await FinalizeAsync(userId, ws, record, order, record.ConfirmedBy ?? "User", ct);
                }
                else if (order.Status is OrderStatus.Failed or OrderStatus.Canceled)
                {
                    // Payment didn't go through: back to verification so the user can edit or try again.
                    await store.UpdateForUserAsync(userId, w =>
                    {
                        var r = w.FindRecord(key);
                        if (r?.Status == RecordStatus.AwaitingPayment) r.Status = RecordStatus.ReadyForVerification;
                        return 0;
                    }, ct);
                }
                else
                {
                    return new RecordContext(ws, record, order);
                }

                ws = await store.GetForUserAsync(userId, ct) ?? throw new WorkspaceExpiredException();
                record = ws.FindRecord(key) ?? throw new WorkflowException("Record not found.");
            }
        }

        return new RecordContext(ws, record, order);
    }

    /// <summary>Brings every record that is waiting on a payment up to date (used by the dashboard).</summary>
    public async Task RefreshPaymentsAsync(string userId, CancellationToken ct = default)
    {
        var ws = await store.GetForUserAsync(userId, ct);
        if (ws is null) return;
        foreach (var key in ws.Records.Where(r => r.Status == RecordStatus.AwaitingPayment).Select(r => r.Key).ToList())
            await LoadRecordAsync(userId, key, ct);
    }

    public async Task<RecordContext> RequireRecordAsync(string userId, string key, CancellationToken ct = default) =>
        await LoadRecordAsync(userId, key, ct) ?? throw new WorkflowException("That record is not part of your current working session.");

    // ───────────────────────── Call timer ─────────────────────────

    /// <summary>
    /// Opening a record starts its first call. With <paramref name="again"/> a new call is started once the previous
    /// one has ended ("Call again"). A caller is on one call at a time, so any other open call is ended first.
    /// </summary>
    public async Task<RecordContext> StartCallAsync(string userId, string key, bool again = false, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var changed = new List<(WorkingRecord Record, CallEntry Call)>();
        var ctx = await store.UpdateForUserAsync(userId, w =>
        {
            var r = w.FindRecord(key) ?? throw new WorkflowException("Record not found.");
            if (r.Calls.Count == 0 || (again && !r.IsOnCall))
            {
                foreach (var other in w.Records.Where(x => x != r && x.IsOnCall))
                {
                    other.LastCall!.EndedAtUtc = now;
                    changed.Add((other, other.LastCall));
                }
                var call = new CallEntry { StartedAtUtc = now };
                r.Calls.Add(call);
                changed.Add((r, call));
            }
            return new RecordContext(w, r, null);
        }, ct);
        await SaveCallsAsync(userId, ctx.Workspace.FileName, changed, ct);
        return ctx;
    }

    /// <summary>Ends the record's current call (dashboard Close Time).</summary>
    public async Task<RecordContext> EndCallAsync(string userId, string key, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var ended = false;
        var ctx = await store.UpdateForUserAsync(userId, w =>
        {
            var r = w.FindRecord(key) ?? throw new WorkflowException("Record not found.");
            if (r.IsOnCall)
            {
                r.LastCall!.EndedAtUtc = now;
                ended = true;
            }
            return new RecordContext(w, r, null);
        }, ct);
        if (ended) await SaveCallsAsync(userId, ctx.Workspace.FileName, [(ctx.Record, ctx.Record.LastCall!)], ct);
        return ctx;
    }

    /// <summary>Saves the outcome and notes of the record's latest call, once that call has ended.</summary>
    public async Task<RecordContext> SetCallOutcomeAsync(string userId, string key, CallOutcome? outcome, string? notes, CancellationToken ct = default)
    {
        if (outcome is { } o && !Enum.IsDefined(o)) throw new WorkflowException("Choose a valid call outcome.");
        notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (notes is { Length: > CallEntry.MaxNotesLength })
            throw new WorkflowException($"Call notes must be {CallEntry.MaxNotesLength} characters or fewer.");

        var ctx = await store.UpdateForUserAsync(userId, w =>
        {
            var r = w.FindRecord(key) ?? throw new WorkflowException("Record not found.");
            var call = r.LastCall ?? throw new WorkflowException("Start a call before saving its outcome.");
            if (call.EndedAtUtc is null) throw new WorkflowException("End the call before saving its outcome.");
            call.Outcome = outcome;
            call.Notes = notes;
            return new RecordContext(w, r, null);
        }, ct);
        await SaveCallsAsync(userId, ctx.Workspace.FileName, [(ctx.Record, ctx.Record.LastCall!)], ct);
        return ctx;
    }

    /// <summary>Writes calls to the permanent call log (insert or update by the call's id).</summary>
    private async Task SaveCallsAsync(string userId, string? sourceFile, IReadOnlyList<(WorkingRecord Record, CallEntry Call)> calls, CancellationToken ct)
    {
        if (calls.Count == 0) return;
        var ids = calls.Select(c => c.Call.Id).ToList();
        var existing = await db.CallLogs.Where(c => ids.Contains(c.PublicId)).ToDictionaryAsync(c => c.PublicId, ct);
        var now = clock.GetUtcNow().UtcDateTime;

        foreach (var (record, call) in calls)
        {
            if (!existing.TryGetValue(call.Id, out var log))
            {
                log = new CallLog { PublicId = call.Id, UserId = userId, ContactId = record.ContactId, SourceFile = sourceFile };
                db.CallLogs.Add(log);
                existing[call.Id] = log;
            }
            else if (log.UserId != userId)
            {
                continue; // a call id always belongs to the caller who made it
            }
            log.StoreName = record.StoreName is { Length: > 250 } name ? name[..250] : record.StoreName;
            log.StartedAtUtc = call.StartedAtUtc.UtcDateTime;
            log.EndedAtUtc = call.EndedAtUtc?.UtcDateTime;
            log.Outcome = call.Outcome;
            log.Notes = call.Notes;
            log.UpdatedAtUtc = now;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A working session is ending: calls still open in the call log are closed now.</summary>
    private async Task CloseOpenCallsAsync(string userId, CancellationToken ct)
    {
        var open = await db.CallLogs.Where(c => c.UserId == userId && c.EndedAtUtc == null).ToListAsync(ct);
        if (open.Count == 0) return;
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var c in open)
        {
            c.EndedAtUtc = now;
            c.UpdatedAtUtc = now;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Shows earlier calls (from previous sessions) for the same Contact IDs in the new call list.</summary>
    private async Task LoadCallHistoryAsync(string userId, Workspace workspace, CancellationToken ct)
    {
        const int maxCallsPerStore = 50;
        var contactIds = workspace.Records.Where(r => !r.ContactIdGenerated).Select(r => r.ContactId).Distinct().ToList();
        if (contactIds.Count == 0) return;

        var logs = await db.CallLogs.AsNoTracking()
            .Where(c => c.UserId == userId && contactIds.Contains(c.ContactId))
            .OrderBy(c => c.StartedAtUtc)
            .ToListAsync(ct);
        var byContact = logs.GroupBy(c => c.ContactId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.TakeLast(maxCallsPerStore).ToList(), StringComparer.OrdinalIgnoreCase);

        static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
        foreach (var record in workspace.Records.Where(r => !r.ContactIdGenerated))
        {
            if (!byContact.TryGetValue(record.ContactId, out var history)) continue;
            record.Calls = history.Select(c => new CallEntry
            {
                Id = c.PublicId,
                StartedAtUtc = Utc(c.StartedAtUtc),
                EndedAtUtc = c.EndedAtUtc is { } ended ? Utc(ended) : null,
                Outcome = c.Outcome,
                Notes = c.Notes,
            }).ToList();
        }
    }

    // ───────────────────────── Dashboard edits ─────────────────────────

    public async Task SetResponseAsync(string userId, string key, string? value, CancellationToken ct = default)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
        if (normalized is not (null or "Y" or "N")) throw new WorkflowException("Response must be Y or N.");

        await store.UpdateForUserAsync(userId, w =>
        {
            var r = w.FindRecord(key) ?? throw new WorkflowException("Record not found.");
            if (r.IsClosed) throw new WorkflowException("This record has been confirmed and can no longer be changed.");
            r.Response = normalized;
            return 0;
        }, ct);
    }

    public async Task SetAdminColumnValueAsync(string userId, string key, int slot, string? value, CancellationToken ct = default)
    {
        var column = (await config.GetAdminColumnsAsync(ct)).FirstOrDefault(c => c.Slot == slot);
        if (column is not { IsActive: true, IsVisible: true, AllowUserEdit: true })
            throw new WorkflowException("This column cannot be edited.");

        var normalized = FieldValidator.Normalize(column.FieldType, value);
        var error = FieldValidator.Validate(column.DisplayLabel, column.FieldType, false, 250, column.OptionList, normalized);
        if (error is not null) throw new WorkflowException(error);

        await store.UpdateForUserAsync(userId, w =>
        {
            var r = w.FindRecord(key) ?? throw new WorkflowException("Record not found.");
            if (r.IsClosed) throw new WorkflowException("This record has been confirmed and can no longer be changed.");
            if (normalized is null) r.AdminColumnValues.Remove(slot);
            else r.AdminColumnValues[slot] = normalized;
            return 0;
        }, ct);
    }

    // ───────────────────────── Confirmation & payment ─────────────────────────

    public async Task<IReadOnlyList<string>> MissingRequiredColumnsAsync(RecordContext ctx, CancellationToken ct = default)
    {
        var resolver = new AdminColumnResolver(ctx.Workspace.Headers, await config.GetFormFieldsAsync(ct));
        return (await config.GetAdminColumnsAsync(ct))
            .Where(c => c.IsActive && c.IsRequired && resolver.Resolve(c, ctx.Record) is null)
            .Select(c => c.DisplayLabel)
            .ToList();
    }

    /// <summary>
    /// The owner has verified the record: lock it, stop the share link, create the order from the form's billing
    /// section and return the payment provider's page. A record that is already linked to a paid order (e.g. after
    /// a lost session) is finalized straight away without charging again.
    /// </summary>
    public async Task<ConfirmResult> ConfirmAndPayAsync(
        string userId, string key, PaymentMethodKind method, string confirmerName, Func<Order, PaymentUrls> buildUrls, CancellationToken ct = default)
    {
        var ctx = await RequireRecordAsync(userId, key, ct);
        var record = ctx.Record;

        if (record.IsClosed && ctx.Order is { } closedOrder)
        {
            var existing = await db.FinalizedRecords.AsNoTracking().FirstOrDefaultAsync(f => f.OrderId == closedOrder.Id, ct);
            return new ConfirmResult(closedOrder, null, existing);
        }
        if (record.Fields is null)
            throw new WorkflowException("Generate and complete the form before confirming.");

        var missing = record.Fields.Where(f => f.IsRequired && f.IsMissing).Select(f => f.Label).ToList();
        if (missing.Count > 0)
            throw new WorkflowException($"Please complete the required field(s): {string.Join(", ", missing)}.");

        var missingColumns = await MissingRequiredColumnsAsync(ctx, ct);
        if (missingColumns.Count > 0)
            throw new WorkflowException($"Please fill in {string.Join(", ", missingColumns)} on the dashboard before confirming.");

        var details = FormBuilder.ReadBilling(record, method, out var billingProblems)
            ?? throw new WorkflowException($"Please correct the billing details: {string.Join(" ", billingProblems)}");

        if (ctx.Order is { Status: OrderStatus.Paid } paidOrder)
        {
            var finalized = await FinalizeAsync(userId, ctx.Workspace, record, paidOrder, confirmerName, ct);
            return new ConfirmResult(paidOrder, null, finalized);
        }
        if (ctx.Order is { Status: OrderStatus.Pending } pending)
        {
            await orders.CancelAsync(pending, ct);
            if (pending.Status == OrderStatus.Paid)
                throw new WorkflowException("A payment for this record was just completed. Refresh the page to continue.");
        }
        if (ctx.Order is { Status: OrderStatus.Processing })
            throw new WorkflowException("A payment for this record is still processing.");

        var pricing = await settings.GetPricingAsync(ct);
        var (order, redirectUrl) = await orders.StartCheckoutAsync(userId, details, record.ContactId, record.StoreName, pricing, buildUrls, ct);

        var now = clock.GetUtcNow();
        string? shareHash = null;
        await store.UpdateForUserAsync(userId, w =>
        {
            var r = w.FindRecord(key) ?? throw new WorkflowException("Record not found.");
            r.OrderPublicId = order.PublicId;
            r.OrderNumber = order.OrderNumber;
            r.Status = RecordStatus.AwaitingPayment;
            r.ConfirmedAtUtc = now;
            r.ConfirmedBy = confirmerName;
            // The recipient can't change anything once the owner has confirmed.
            shareHash = r.Share?.TokenHash;
            if (r.Share is not null) r.Share.ExpiresAtUtc = now;
            return 0;
        }, ct);
        if (shareHash is not null) await store.RemoveShareTokenAsync(shareHash, ct);

        await audit.LogAsync(AuditCategories.Record, "RecordConfirmed",
            $"Order {order.OrderNumber}, Contact ID {record.ContactId}: sent to payment", nameof(Order), order.OrderNumber, ct: ct);
        return new ConfirmResult(order, redirectUrl, null);
    }

    public async Task<PaymentReturn> CompletePaymentReturnAsync(string userId, string sessionId, CancellationToken ct = default)
    {
        var order = await orders.GetBySessionForUserAsync(userId, sessionId, ct);
        if (order is null) return new PaymentReturn(null, null);
        order = await orders.SyncAsync(order, ct);
        return new PaymentReturn(order, await ApplyOrderToWorkspaceAsync(userId, order, ct));
    }

    public async Task<PaymentReturn> CancelPaymentAsync(string userId, Guid orderPublicId, CancellationToken ct = default)
    {
        var order = await orders.GetForUserAsync(userId, orderPublicId, ct);
        if (order is null) return new PaymentReturn(null, null);
        await orders.CancelAsync(order, ct);
        return new PaymentReturn(order, await ApplyOrderToWorkspaceAsync(userId, order, ct));
    }

    /// <summary>Finds the record paid for by this order and reconciles it (finalizing it when paid).</summary>
    private async Task<string?> ApplyOrderToWorkspaceAsync(string userId, Order order, CancellationToken ct)
    {
        try
        {
            var ws = await store.GetForUserAsync(userId, ct);
            var key = ws?.FindRecordByOrder(order.PublicId)?.Key;
            if (key is null) return null;
            await LoadRecordAsync(userId, key, ct);
            return key;
        }
        catch (WorkspaceExpiredException)
        {
            return null;
        }
    }

    // ───────────────────────── Form ─────────────────────────

    /// <summary>"Proceed" on the dashboard: builds the form (store details + billing details) from the CSV row.</summary>
    public async Task GenerateFormAsync(string userId, string key, CancellationToken ct = default)
    {
        var ctx = await RequireRecordAsync(userId, key, ct);
        if (ctx.Record.IsClosed) throw new WorkflowException("This record has already been completed.");
        if (ctx.Record.Fields is not null) return;

        var definitions = await config.GetFormFieldsAsync(ct);
        await store.UpdateForUserAsync(userId, w =>
        {
            var r = w.FindRecord(key) ?? throw new WorkflowException("Record not found.");
            if (r.Fields is not null) return 0;
            r.Fields = FormBuilder.Build(definitions, r, w.Headers, w.ContactIdHeader, offerBankAccount: paymentOptions.Value.EnableAch);
            r.StoreName = FormBuilder.FindStoreName(r, w.Headers) ?? r.StoreName;
            if (r.Status < RecordStatus.FormGenerated) r.Status = RecordStatus.FormGenerated;
            return 0;
        }, ct);

        await audit.LogAsync(AuditCategories.Record, "FormGenerated", $"Contact ID {ctx.Record.ContactId}", ct: ct);
    }

    /// <summary>Owner edits. Format is validated; required fields are enforced at verification.</summary>
    public async Task<IReadOnlyDictionary<string, string>> SaveFormAsync(
        string userId, string key, IReadOnlyDictionary<string, string?> values, string editorName, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>();
        var now = clock.GetUtcNow();
        await store.UpdateForUserAsync(userId, w =>
        {
            var r = w.FindRecord(key) ?? throw new WorkflowException("Record not found.");
            if (r.IsLocked) throw new WorkflowException("This record has been confirmed and can no longer be edited.");
            if (r.Fields is null) throw new WorkflowException("The form has not been generated yet.");

            var updates = new List<(WorkingField Field, string? Value)>();
            foreach (var f in r.Fields)
            {
                if (!values.TryGetValue(f.Key, out var raw)) continue;
                var v = FieldValidator.Normalize(f.FieldType, raw);
                var error = FieldValidator.Validate(f.Label, f.FieldType, false, f.MaxLength, f.Options, v);
                if (error is not null) errors[f.Key] = error;
                else if (!string.Equals(v, f.Value, StringComparison.Ordinal)) updates.Add((f, v));
            }
            if (errors.Count > 0) return 0;

            foreach (var (f, v) in updates)
            {
                f.Value = v;
                f.Source = v is null ? FieldSource.Empty : FieldSource.User;
                f.UpdatedAtUtc = now;
                f.UpdatedBy = editorName;
            }
            r.StoreName = r.FindField("StoreName")?.Value ?? r.StoreName;
            return updates.Count;
        }, ct);
        return errors;
    }

    // ───────────────────────── Sharing ─────────────────────────

    public async Task<ShareLink> GetOrCreateShareLinkAsync(string userId, string key, bool regenerate, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        string? revokedHash = null;
        var (share, workspaceId, created, contactId) = await store.UpdateForUserAsync(userId, w =>
        {
            var r = w.FindRecord(key) ?? throw new WorkflowException("Record not found.");
            if (r.IsLocked) throw new WorkflowException("This record has already been confirmed.");
            if (r.Fields is null) throw new WorkflowException("Generate the form before sharing it.");

            if (r.Share is { } existing && !regenerate && (existing.IsUsable(now) || existing.SubmittedAtUtc is not null))
                return (existing, w.Id, false, r.ContactId);

            revokedHash = r.Share?.TokenHash;
            var token = SecureTokens.NewToken();
            r.Share = new ShareInfo
            {
                Token = token,
                TokenHash = SecureTokens.Sha256(token),
                CreatedAtUtc = now,
                ExpiresAtUtc = w.AbsoluteExpiresAtUtc,
            };
            r.Status = RecordStatus.SharedPending;
            return (r.Share, w.Id, true, r.ContactId);
        }, ct);

        if (revokedHash is not null) await store.RemoveShareTokenAsync(revokedHash, ct);
        if (created)
        {
            await store.RegisterShareTokenAsync(share.TokenHash, workspaceId, key, share.ExpiresAtUtc, ct);
            await audit.LogAsync(AuditCategories.Record, regenerate ? "ShareLinkRegenerated" : "ShareLinkCreated", $"Contact ID {contactId}", ct: ct);
        }
        return new ShareLink(share.Token, share.ExpiresAtUtc, share.SubmittedAtUtc, share.LastOpenedAtUtc);
    }

    public async Task SendShareEmailAsync(string userId, string key, string toAddress, string? note, Func<string, string> buildShareUrl, CancellationToken ct = default)
    {
        toAddress = toAddress.Trim();
        if (!FieldValidator.IsEmail(toAddress)) throw new WorkflowException("Enter a valid email address.");
        if (note is { Length: > 500 }) throw new WorkflowException("The message must be 500 characters or fewer.");

        var link = await GetOrCreateShareLinkAsync(userId, key, regenerate: false, ct);
        if (link.SubmittedAtUtc is not null)
            throw new WorkflowException("The recipient already submitted this form. Create a new link to request more changes.");

        var sender = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.FullName, Company = u.Company != null ? u.Company.Name : null }).FirstAsync(ct);
        var ctx = await RequireRecordAsync(userId, key, ct);
        var storeName = ctx.Record.StoreName ?? $"Contact ID {ctx.Record.ContactId}";

        var content = EmailTemplates.ShareForm(appOptions.Value.Name, sender.FullName, sender.Company, storeName, note?.Trim(), buildShareUrl(link.Token), link.ExpiresAtUtc);
        try
        {
            await email.SendAsync(toAddress, content.Subject, content.Html, content.Text, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Share email for Contact ID {ContactId} could not be sent.", ctx.Record.ContactId);
            throw new WorkflowException("The email couldn't be sent — the site's email (SMTP) settings may be missing or wrong. " +
                                        "You can copy the link or use WhatsApp / SMS instead.");
        }

        var masked = MaskEmail(toAddress);
        await RecordShareChannelAsync(userId, key, $"Email to {masked}", ct);
    }

    public async Task RecordShareChannelAsync(string userId, string key, string channel, CancellationToken ct = default)
    {
        channel = channel.Length > 80 ? channel[..80] : channel;
        await store.UpdateForUserAsync(userId, w =>
        {
            var r = w.FindRecord(key);
            if (r?.Share is not null && r.Share.SentTo.Count < 50) r.Share.SentTo.Add($"{channel} · {clock.GetUtcNow():u}");
            return 0;
        }, ct);
        await audit.LogAsync(AuditCategories.Record, "FormShared", channel, ct: ct);
    }

    // ───────────────────────── Recipient (anonymous, token-based) ─────────────────────────

    public async Task<SharedFormContext?> GetSharedFormAsync(string token, bool markOpened, CancellationToken ct = default)
    {
        var resolved = await ResolveShareAsync(token, ct);
        if (resolved is null) return null;
        var (ws, record, hash) = resolved.Value;

        Order? order = null;
        if (record.OrderPublicId is Guid orderId)
            order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.PublicId == orderId && o.UserId == ws.OwnerUserId, ct);

        var owner = await db.Users.AsNoTracking().Where(u => u.Id == ws.OwnerUserId)
            .Select(u => new { u.FullName, Company = u.Company != null ? u.Company.Name : null }).FirstOrDefaultAsync(ct);

        if (markOpened && record.Share!.SubmittedAtUtc is null)
        {
            var now = clock.GetUtcNow();
            await store.UpdateByIdAsync(ws.Id, w =>
            {
                if (w.FindRecord(record.Key)?.Share is { } s && s.TokenHash == hash) s.LastOpenedAtUtc = now;
                return 0;
            }, ct);
        }

        return new SharedFormContext(ws, record, order, owner?.FullName ?? "The requester", owner?.Company);
    }

    /// <summary>
    /// Applies the recipient's answers to the owner's working record. Only recipient-editable fields (including
    /// the billing section) can change, and only when the recipient actually changed them. No order exists yet at
    /// this point and card data is never collected here – payment happens after the owner confirms.
    /// </summary>
    public async Task<RecipientSubmitResult> SubmitRecipientAsync(
        string token,
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyDictionary<string, string?> originals,
        string? recipientName,
        Func<string, string> buildRecordUrl,
        CancellationToken ct = default)
    {
        var resolved = await ResolveShareAsync(token, ct);
        if (resolved is null) return new RecipientSubmitResult(RecipientSubmitOutcome.LinkInvalid, new Dictionary<string, string>());
        var (ws, _, hash) = resolved.Value;

        recipientName = string.IsNullOrWhiteSpace(recipientName) ? null : CsvSanitizer.CleanImportedValue(recipientName, 100);
        var errors = new Dictionary<string, string>();
        var now = clock.GetUtcNow();

        var outcome = await store.UpdateByIdAsync(ws.Id, w =>
        {
            var r = w.Records.FirstOrDefault(x => x.Share?.TokenHash == hash);
            if (r?.Share is null || r.Fields is null || r.IsLocked || !r.Share.IsUsable(now))
                return (Ok: false, Record: (WorkingRecord?)null, Changed: 0);

            var updates = new List<(WorkingField Field, string? Value)>();
            foreach (var f in r.Fields.Where(f => f.RecipientEditable))
            {
                if (!values.TryGetValue(f.Key, out var raw)) continue;
                var v = FieldValidator.Normalize(f.FieldType, raw);
                var error = FieldValidator.Validate(f, v);
                if (error is not null) { errors[f.Key] = error; continue; }

                var shown = FieldValidator.Normalize(f.FieldType, originals.GetValueOrDefault(f.Key));
                if (!string.Equals(v, shown, StringComparison.Ordinal) && !string.Equals(v, f.Value, StringComparison.Ordinal))
                    updates.Add((f, v));
            }
            if (errors.Count > 0) return (Ok: false, Record: null, Changed: 0);

            foreach (var (f, v) in updates)
            {
                f.Value = v;
                f.Source = v is null ? FieldSource.Empty : FieldSource.Recipient;
                f.UpdatedAtUtc = now;
                f.UpdatedBy = recipientName ?? "Recipient";
            }
            r.Share.SubmittedAtUtc = now;
            r.RecipientSubmittedAtUtc = now;
            r.RecipientName = recipientName;
            r.StoreName = r.FindField("StoreName")?.Value ?? r.StoreName;
            r.Status = RecordStatus.ReadyForVerification;
            return (Ok: true, Record: r, Changed: updates.Count);
        }, ct);

        if (errors.Count > 0) return new RecipientSubmitResult(RecipientSubmitOutcome.ValidationFailed, errors);
        if (!outcome.Ok || outcome.Record is null) return new RecipientSubmitResult(RecipientSubmitOutcome.LinkInvalid, errors);

        var record = outcome.Record;
        await store.RemoveShareTokenAsync(hash, ct);
        await audit.LogAsync(AuditCategories.Record, "RecipientSubmitted",
            $"Contact ID {record.ContactId}: {outcome.Changed} field(s) updated", nameof(Order), record.OrderNumber,
            userId: null, userName: "recipient", ct: ct);

        await NotifyOwnerAsync(ws.OwnerUserId, record, buildRecordUrl(record.Key), ct);
        return new RecipientSubmitResult(RecipientSubmitOutcome.Submitted, errors);
    }

    private async Task<(Workspace Workspace, WorkingRecord Record, string Hash)?> ResolveShareAsync(string token, CancellationToken ct)
    {
        if (!SecureTokens.LooksLikeToken(token, 30, 64)) return null;
        var hash = SecureTokens.Sha256(token);
        var pointer = await store.ResolveShareTokenAsync(hash, ct);
        if (pointer is null) return null;

        var ws = await store.GetByIdAsync(pointer.WorkspaceId, ct);
        var record = ws?.FindRecord(pointer.RecordKey);
        if (ws is null || record?.Share is null || record.Fields is null || record.IsLocked) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(record.Share.TokenHash), Encoding.ASCII.GetBytes(hash)))
            return null;
        if (record.Share.ExpiresAtUtc <= clock.GetUtcNow()) return null;
        return (ws, record, hash);
    }

    private async Task NotifyOwnerAsync(string ownerId, WorkingRecord record, string recordUrl, CancellationToken ct)
    {
        try
        {
            var owner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == ownerId, ct);
            if (owner?.Email is null) return;
            var content = EmailTemplates.RecipientSubmitted(appOptions.Value.Name, owner.FullName, record.ContactId,
                record.StoreName ?? record.ContactId, recordUrl);
            await email.SendAsync(owner.Email, content.Subject, content.Html, content.Text, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not notify the record owner about a recipient submission.");
        }
    }

    // ───────────────────────── Completion ─────────────────────────

    /// <summary>
    /// Runs once the confirmed record's order is paid: saves the final values permanently and purges the
    /// temporary CSV/form data from the workspace.
    /// </summary>
    private async Task<FinalizedRecord> FinalizeAsync(string userId, Workspace workspace, WorkingRecord record, Order order, string confirmerName, CancellationToken ct)
    {
        var key = record.Key;
        if (record.IsClosed && record.ConfirmationNumber is not null)
            return await db.FinalizedRecords.AsNoTracking().FirstAsync(f => f.ConfirmationNumber == record.ConfirmationNumber && f.UserId == userId, ct);

        if (order.Status != OrderStatus.Paid)
            throw new WorkflowException("Payment has not been completed for this record.");
        if (record.Fields is null)
            throw new WorkflowException("Generate and complete the form before confirming.");

        var columns = await config.GetAdminColumnsAsync(ct);
        var resolver = new AdminColumnResolver(workspace.Headers, await config.GetFormFieldsAsync(ct));
        var snapshot = resolver.Snapshot(columns, record);
        var companyName = await db.Users.Where(u => u.Id == userId).Select(u => u.Company != null ? u.Company.Name : null).FirstOrDefaultAsync(ct);
        var now = clock.GetUtcNow();

        var finalized = await db.FinalizedRecords.FirstOrDefaultAsync(f => f.OrderId == order.Id, ct);
        if (finalized is null)
        {
            finalized = new FinalizedRecord
            {
                ConfirmationNumber = SecureTokens.NewConfirmationNumber(),
                OrderId = order.Id,
                UserId = userId,
                CompanyName = companyName,
                ContactId = record.ContactId,
                StoreName = record.FindField("StoreName")?.Value ?? record.StoreName,
                Response = record.Response,
                RecordCreatedAtUtc = record.CreatedAtUtc.UtcDateTime,
                RecipientSubmittedAtUtc = record.RecipientSubmittedAtUtc?.UtcDateTime,
                ConfirmedAtUtc = (record.ConfirmedAtUtc ?? now).UtcDateTime,
                ConfirmedByUserId = userId,
                ConfirmedByName = confirmerName,
                ClosedAtUtc = now.UtcDateTime,
            };
            finalized.SetFields(record.Fields
                .OrderBy(f => f.Section).ThenBy(f => f.Order)
                .Select(f => new FinalizedField(f.Section.Name(), f.Label, f.Value, f.Source.ToString())));
            finalized.SetAdminColumns(columns
                .Where(c => snapshot.ContainsKey(c.Slot))
                .OrderBy(c => c.DisplayOrder)
                .Select(c => new FinalizedField("Dashboard", c.DisplayLabel, snapshot[c.Slot], "Column")));
            db.FinalizedRecords.Add(finalized);
            await db.SaveChangesAsync(ct);
        }

        // Close the working record and purge its temporary CSV/form data from the workspace.
        string? shareHash = null;
        try
        {
            await store.UpdateForUserAsync(userId, w =>
            {
                var r = w.FindRecord(key);
                if (r is null) return 0;
                shareHash = r.Share?.TokenHash;
                r.Status = RecordStatus.Verified;
                r.OrderPublicId = order.PublicId;
                r.OrderNumber = order.OrderNumber;
                r.ConfirmedAtUtc ??= now;
                r.ConfirmedBy ??= confirmerName;
                r.ClosedAtUtc = now;
                r.ConfirmationNumber = finalized.ConfirmationNumber;
                r.ClosedAdminColumnValues = snapshot;
                r.StoreName = finalized.StoreName;
                r.CsvValues = [];
                r.Fields = null;
                r.Share = null;
                return 0;
            }, ct);
        }
        catch (WorkspaceExpiredException)
        {
            // The permanent record is saved; the temporary workspace is already gone.
        }
        if (shareHash is not null) await store.RemoveShareTokenAsync(shareHash, ct);

        await audit.LogAsync(AuditCategories.Record, "RecordCompleted",
            $"Order {order.OrderNumber}, Contact ID {record.ContactId}", nameof(FinalizedRecord), finalized.ConfirmationNumber, ct: ct);
        return finalized;
    }

    private static string MaskEmail(string address)
    {
        var at = address.IndexOf('@');
        return at <= 1 ? "***" + address[Math.Max(at, 0)..] : $"{address[0]}***{address[at..]}";
    }
}
