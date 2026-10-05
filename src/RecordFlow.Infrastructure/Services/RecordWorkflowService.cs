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

public enum RecipientSubmitOutcome { Submitted, ValidationFailed, LinkInvalid }

public sealed record RecipientSubmitResult(RecipientSubmitOutcome Outcome, IReadOnlyDictionary<string, string> Errors);

/// <summary>
/// Orchestrates the end-to-end workflow:
/// Upload CSV → select record → checkout details → payment → generate form → share → recipient
/// completes → automatic update → verify → confirm → receipt.
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
        var relinked = await RelinkPaidOrdersAsync(userId, workspace, ct);
        if (relinked > 0)
            result.Warnings.Add($"{relinked} record(s) were matched to orders you already paid for and can continue from “Generate form”.");

        await store.SaveNewAsync(workspace, ct);
        // Only metadata is logged – never CSV contents.
        await audit.LogAsync(AuditCategories.Workspace, "CsvImported",
            $"{safeName}: {result.Records.Count} record(s), {result.Headers.Count} column(s)", ct: ct);
        return result;
    }

    /// <summary>
    /// If a previous session ended after payment but before confirmation, re-uploading the CSV re-attaches
    /// the paid order to the matching Contact ID so the customer never pays twice.
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
            record.Status = RecordStatus.Paid;
            linked++;
        }
        return linked;
    }

    public async Task EndSessionAsync(string userId, string reason, CancellationToken ct = default)
    {
        await store.DeleteForUserAsync(userId, ct);
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

            if (order is not null && StatusAfterPayment(record, order) != record.Status)
            {
                ws = await store.UpdateForUserAsync(userId, w =>
                {
                    var r = w.FindRecord(key);
                    if (r is not null) r.Status = StatusAfterPayment(r, order);
                    return w;
                }, ct);
                record = ws.FindRecord(key)!;
            }
        }

        return new RecordContext(ws, record, order);
    }

    public async Task<RecordContext> RequireRecordAsync(string userId, string key, CancellationToken ct = default) =>
        await LoadRecordAsync(userId, key, ct) ?? throw new WorkflowException("That record is not part of your current working session.");

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

    // ───────────────────────── Checkout & payment ─────────────────────────

    public async Task<IReadOnlyList<string>> MissingRequiredColumnsAsync(RecordContext ctx, CancellationToken ct = default)
    {
        var resolver = new AdminColumnResolver(ctx.Workspace.Headers, await config.GetFormFieldsAsync(ct));
        return (await config.GetAdminColumnsAsync(ct))
            .Where(c => c.IsActive && c.IsRequired && resolver.Resolve(c, ctx.Record) is null)
            .Select(c => c.DisplayLabel)
            .ToList();
    }

    /// <summary>Saves the billing details, creates the order and returns the provider's payment page URL.</summary>
    public async Task<string> StartCheckoutAsync(string userId, string key, CheckoutDetails details, Func<Order, PaymentUrls> buildUrls, CancellationToken ct = default)
    {
        var ctx = await RequireRecordAsync(userId, key, ct);
        var record = ctx.Record;
        if (record.IsPaidOrLater) throw new WorkflowException("This record has already been paid for.");

        var missing = await MissingRequiredColumnsAsync(ctx, ct);
        if (missing.Count > 0)
            throw new WorkflowException($"Please fill in {string.Join(", ", missing)} on the dashboard before checkout.");

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

        await store.UpdateForUserAsync(userId, w =>
        {
            var r = w.FindRecord(key) ?? throw new WorkflowException("Record not found.");
            r.Checkout = details;
            r.OrderPublicId = order.PublicId;
            r.OrderNumber = order.OrderNumber;
            r.Status = RecordStatus.AwaitingPayment;
            return 0;
        }, ct);

        return redirectUrl;
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

    private async Task<string?> ApplyOrderToWorkspaceAsync(string userId, Order order, CancellationToken ct)
    {
        try
        {
            return await store.UpdateForUserAsync(userId, w =>
            {
                var r = w.FindRecordByOrder(order.PublicId);
                if (r is null) return null;
                r.Status = StatusAfterPayment(r, order);
                return r.Key;
            }, ct);
        }
        catch (WorkspaceExpiredException)
        {
            return null;
        }
    }

    private static RecordStatus StatusAfterPayment(WorkingRecord r, Order order) => r.Status switch
    {
        RecordStatus.AwaitingPayment when order.Status == OrderStatus.Paid => RecordStatus.Paid,
        RecordStatus.AwaitingPayment when order.Status is OrderStatus.Failed or OrderStatus.Canceled => RecordStatus.CheckoutStarted,
        _ => r.Status,
    };

    // ───────────────────────── Form ─────────────────────────

    public async Task GenerateFormAsync(string userId, string key, CancellationToken ct = default)
    {
        var ctx = await RequireRecordAsync(userId, key, ct);
        if (ctx.Order is not { Status: OrderStatus.Paid })
            throw new WorkflowException("Payment must be completed before the form can be generated.");
        if (ctx.Record.Fields is not null) return;

        var definitions = await config.GetFormFieldsAsync(ct);
        await store.UpdateForUserAsync(userId, w =>
        {
            var r = w.FindRecord(key) ?? throw new WorkflowException("Record not found.");
            if (r.Fields is not null) return 0;
            r.Fields = FormBuilder.Build(definitions, r, w.Headers, w.ContactIdHeader);
            r.StoreName = FormBuilder.FindStoreName(r, w.Headers) ?? r.StoreName;
            if (r.Status < RecordStatus.FormGenerated) r.Status = RecordStatus.FormGenerated;
            return 0;
        }, ct);

        await audit.LogAsync(AuditCategories.Record, "FormGenerated", $"Contact ID {ctx.Record.ContactId}", nameof(Order), ctx.Order.OrderNumber, ct: ct);
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
            if (r.IsClosed) throw new WorkflowException("This record has been confirmed and can no longer be edited.");
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
            if (r.IsClosed) throw new WorkflowException("This record has already been confirmed.");
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
        await email.SendAsync(toAddress, content.Subject, content.Html, content.Text, ct);

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
    /// Applies the recipient's answers to the owner's working record. Only fields the administrator marked
    /// recipient-editable can change, and only when the recipient actually changed them; payment, billing and
    /// order data are not part of the form and cannot be modified here.
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
            if (r?.Share is null || r.Fields is null || r.IsClosed || !r.Share.IsUsable(now))
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
        if (ws is null || record?.Share is null || record.Fields is null || record.IsClosed) return null;
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

    // ───────────────────────── Verification & confirmation ─────────────────────────

    public async Task<FinalizedRecord> ConfirmAsync(string userId, string key, string confirmerName, CancellationToken ct = default)
    {
        var ctx = await RequireRecordAsync(userId, key, ct);
        var record = ctx.Record;

        if (record.IsClosed && record.ConfirmationNumber is not null)
            return await db.FinalizedRecords.AsNoTracking().FirstAsync(f => f.ConfirmationNumber == record.ConfirmationNumber && f.UserId == userId, ct);

        if (ctx.Order is not { Status: OrderStatus.Paid } order)
            throw new WorkflowException("Payment has not been completed for this record.");
        if (record.Fields is null)
            throw new WorkflowException("Generate and complete the form before confirming.");

        var missing = record.Fields.Where(f => f.IsRequired && f.IsMissing).Select(f => f.Label).ToList();
        if (missing.Count > 0)
            throw new WorkflowException($"Please complete the required field(s): {string.Join(", ", missing)}.");

        var columns = await config.GetAdminColumnsAsync(ct);
        var resolver = new AdminColumnResolver(ctx.Workspace.Headers, await config.GetFormFieldsAsync(ct));
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
                ConfirmedAtUtc = now.UtcDateTime,
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
                r.ConfirmedAtUtc = now;
                r.ConfirmedBy = confirmerName;
                r.ClosedAtUtc = now;
                r.ConfirmationNumber = finalized.ConfirmationNumber;
                r.ClosedAdminColumnValues = snapshot;
                r.StoreName = finalized.StoreName;
                r.CsvValues = [];
                r.Fields = null;
                r.Share = null;
                r.Checkout = null;
                return 0;
            }, ct);
        }
        catch (WorkspaceExpiredException)
        {
            // The permanent record is saved; the temporary workspace is already gone.
        }
        if (shareHash is not null) await store.RemoveShareTokenAsync(shareHash, ct);

        await audit.LogAsync(AuditCategories.Record, "RecordConfirmed",
            $"Order {order.OrderNumber}, Contact ID {record.ContactId}", nameof(FinalizedRecord), finalized.ConfirmationNumber, ct: ct);
        return finalized;
    }

    private static string MaskEmail(string address)
    {
        var at = address.IndexOf('@');
        return at <= 1 ? "***" + address[Math.Max(at, 0)..] : $"{address[0]}***{address[at..]}";
    }
}
