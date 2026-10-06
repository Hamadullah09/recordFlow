using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RecordFlow.Core;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Entities;
using RecordFlow.Core.Services;
using RecordFlow.Core.Workspaces;
using RecordFlow.Infrastructure.Csv;
using RecordFlow.Infrastructure.Data;
using RecordFlow.Infrastructure.Payments;
using RecordFlow.Infrastructure.Services;
using RecordFlow.Infrastructure.Settings;
using RecordFlow.Infrastructure.Workspaces;

namespace RecordFlow.Tests;

/// <summary>
/// End-to-end workflow against an in-memory database and the simulated payment provider:
/// upload → form → share → recipient (incl. billing) → verify/confirm → payment.
/// </summary>
public class WorkflowIntegrationTests : IDisposable
{
    private const string Csv =
        "Contact ID,Store Name,Address Line 1,City,State,ZIP Code,Store Phone Number,Owner Name,Region\n" +
        "C-1,Main Street Market,125 Main St,Springfield,IL,62701,(217) 555-0142,,Midwest\n" +
        "C-2,Harbor Grocery,48 Harbor Rd,Portland,ME,04101,,Ann Lee,Northeast\n";

    private static readonly Func<Order, PaymentUrls> Urls = _ => new PaymentUrls("https://x/return?session_id={CHECKOUT_SESSION_ID}", "https://x/cancel");

    private static readonly Dictionary<string, string?> Billing = new()
    {
        [FormBuilder.BillingName] = "Maria Lopez",
        [FormBuilder.BillingEmail] = "maria@store.test",
        [FormBuilder.BillingAddress1] = "125 Main St",
        [FormBuilder.BillingCity] = "Springfield",
        [FormBuilder.BillingState] = "IL",
        [FormBuilder.BillingZip] = "62701",
    };

    private readonly ApplicationDbContext _db;
    private readonly MemoryCache _memory = new(new MemoryCacheOptions());
    private readonly DistributedWorkspaceStore _store;
    private readonly SimulatedPaymentProvider _payments;
    private readonly RecordWorkflowService _workflow;
    private readonly CapturingEmailSender _email = new();

    public WorkflowIntegrationTests()
    {
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new ApplicationDbContext(dbOptions);
        _db.Users.AddRange(
            new ApplicationUser { Id = "user-a", UserName = "alice", Email = "alice@test.local", FullName = "Alice Adams" },
            new ApplicationUser { Id = "user-b", UserName = "bob", Email = "bob@test.local", FullName = "Bob Brown" });
        _db.FormFields.AddRange(
            new FormFieldDefinition { Key = "StoreName", Label = "Store Name", Section = FormSection.Store, CsvAliases = "Store Name", IsRequired = true, IsActive = true, RecipientEditable = false },
            new FormFieldDefinition { Key = "StorePhone", Label = "Store Phone", Section = FormSection.Store, FieldType = FieldType.Phone, CsvAliases = "Store Phone Number", IsRequired = true, IsActive = true, RecipientEditable = true },
            new FormFieldDefinition { Key = "OwnerName", Label = "Owner Name", Section = FormSection.Owner, CsvAliases = "Owner Name", IsRequired = true, IsActive = true, RecipientEditable = true });
        _db.AdminColumns.Add(new AdminColumn { Slot = 1, Name = "region", DisplayLabel = "Region", SourceField = "Region", IsActive = true });
        _db.SaveChanges();

        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        _store = new DistributedWorkspaceStore(cache, new EphemeralDataProtectionProvider(), Options.Create(new WorkspaceOptions()), TimeProvider.System, NullLogger<DistributedWorkspaceStore>.Instance);
        _payments = new SimulatedPaymentProvider(_memory);
        var audit = new NoopAudit();
        var orders = new OrderService(_db, _payments, audit, TimeProvider.System, NullLogger<OrderService>.Instance);
        _workflow = new RecordWorkflowService(
            _store, new CsvImportService(Options.Create(new CsvImportOptions()), TimeProvider.System), _db, orders,
            new PortalConfigService(_db, _memory), new AppSettingsService(_db, _memory), audit, _email,
            Options.Create(new AppOptions()), Options.Create(new PaymentOptions { EnableAch = true }), TimeProvider.System,
            NullLogger<RecordWorkflowService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _memory.Dispose();
    }

    private async Task<string> ImportAsync(string userId = "user-a", string contactId = "C-1")
    {
        var import = await _workflow.ImportCsvAsync(userId, new MemoryStream(Encoding.UTF8.GetBytes(Csv)), "stores.csv");
        Assert.True(import.Succeeded);
        return (await _workflow.GetWorkspaceAsync(userId))!.Records.Single(r => r.ContactId == contactId).Key;
    }

    /// <summary>Upload → Proceed (form) → owner completes the form incl. billing → ready to confirm.</summary>
    private async Task<string> ImportAndCompleteFormAsync(string userId = "user-a")
    {
        var key = await ImportAsync(userId);
        await _workflow.GenerateFormAsync(userId, key);
        var values = new Dictionary<string, string?>(Billing) { ["OwnerName"] = "Maria Lopez" };
        Assert.Empty(await _workflow.SaveFormAsync(userId, key, values, "Alice Adams"));
        return key;
    }

    private async Task<Order> PayAsync(string userId, Order order, bool approve = true)
    {
        _payments.Complete(order.ProviderSessionId!, approve);
        var result = await _workflow.CompletePaymentReturnAsync(userId, order.ProviderSessionId!);
        return result.Order!;
    }

    [Fact]
    public async Task Full_workflow_from_upload_to_payment()
    {
        var key = await ImportAsync();

        // Proceed: the form is generated straight away – no payment first.
        await _workflow.GenerateFormAsync("user-a", key);
        var record = (await _workflow.LoadRecordAsync("user-a", key))!.Record;
        Assert.Equal(RecordStatus.FormGenerated, record.Status);
        Assert.True(record.FindField("OwnerName")!.IsMissing);              // missing in CSV → empty, editable
        Assert.Equal("(217) 555-0142", record.FindField("StorePhone")!.Value);
        Assert.Contains(record.Fields!, f => f.Section == FormSection.Billing && f.RecipientEditable);
        Assert.Empty(_db.Orders);

        // Share; the recipient completes the store details and fills in the billing details.
        var link = await _workflow.GetOrCreateShareLinkAsync("user-a", key, regenerate: false);
        Assert.NotNull(await _workflow.GetSharedFormAsync(link.Token, markOpened: true));

        var values = new Dictionary<string, string?>(Billing)
        {
            ["OwnerName"] = "Maria Lopez",
            ["StorePhone"] = "(217) 555-0142",
            ["StoreName"] = "Hijacked Name",                                 // not recipient-editable → ignored
        };
        var submit = await _workflow.SubmitRecipientAsync(link.Token, values,
            originals: new Dictionary<string, string?> { ["OwnerName"] = null, ["StorePhone"] = "(217) 555-0142" },
            recipientName: "Maria", buildRecordUrl: k => $"https://x/records/{k}/verify");
        Assert.Equal(RecipientSubmitOutcome.Submitted, submit.Outcome);

        record = (await _workflow.LoadRecordAsync("user-a", key))!.Record;
        Assert.Equal(RecordStatus.ReadyForVerification, record.Status);
        Assert.Equal("Maria Lopez", record.FindField("OwnerName")!.Value);
        Assert.Equal(FieldSource.Recipient, record.FindField(FormBuilder.BillingEmail)!.Source);
        Assert.Equal("Main Street Market", record.FindField("StoreName")!.Value);
        Assert.Equal(FieldSource.Csv, record.FindField("StorePhone")!.Source); // unchanged value isn't attributed to recipient
        Assert.Single(_email.Sent, m => m.To == "alice@test.local");        // owner notified automatically

        // The link works only once.
        Assert.Null(await _workflow.GetSharedFormAsync(link.Token, markOpened: false));

        // Verify & confirm → the order is created from the (recipient-edited) billing details → payment page.
        var confirm = await _workflow.ConfirmAndPayAsync("user-a", key, PaymentMethodKind.Card, "Alice Adams", Urls);
        Assert.NotNull(confirm.PaymentUrl);
        Assert.Null(confirm.Finalized);
        Assert.Equal(49.00m, confirm.Order.Total);
        Assert.Equal("Maria Lopez", confirm.Order.BillingName);
        Assert.Equal("maria@store.test", confirm.Order.BillingEmail);
        record = (await _workflow.LoadRecordAsync("user-a", key))!.Record;
        Assert.Equal(RecordStatus.AwaitingPayment, record.Status);
        await Assert.ThrowsAsync<WorkflowException>(() =>                  // locked while paying
            _workflow.SaveFormAsync("user-a", key, new Dictionary<string, string?> { ["OwnerName"] = "X" }, "Alice"));

        // Payment succeeds → only the final values become permanent; the temporary CSV data is purged.
        var paid = await PayAsync("user-a", confirm.Order);
        Assert.Equal(OrderStatus.Paid, paid.Status);
        var finalized = await _db.FinalizedRecords.SingleAsync(f => f.OrderId == paid.Id);
        Assert.Matches("^CNF-", finalized.ConfirmationNumber);
        Assert.Contains(finalized.GetFields(), f => f.Label == "Owner Name" && f.Value == "Maria Lopez");
        Assert.Contains(finalized.GetAdminColumns(), c => c.Label == "Region" && c.Value == "Midwest");

        record = (await _workflow.GetWorkspaceAsync("user-a"))!.FindRecord(key)!;
        Assert.Equal(RecordStatus.Verified, record.Status);
        Assert.NotNull(record.ClosedAtUtc);
        Assert.Empty(record.CsvValues);
        Assert.Null(record.Fields);
    }

    [Fact]
    public async Task Declined_payment_unlocks_the_record_for_another_attempt()
    {
        var key = await ImportAndCompleteFormAsync();
        var first = await _workflow.ConfirmAndPayAsync("user-a", key, PaymentMethodKind.Card, "Alice Adams", Urls);

        await PayAsync("user-a", first.Order, approve: false);
        var record = (await _workflow.LoadRecordAsync("user-a", key))!.Record;
        Assert.Equal(RecordStatus.ReadyForVerification, record.Status);
        Assert.Empty(_db.FinalizedRecords);

        var second = await _workflow.ConfirmAndPayAsync("user-a", key, PaymentMethodKind.Card, "Alice Adams", Urls);
        Assert.NotEqual(first.Order.Id, second.Order.Id);
        await PayAsync("user-a", second.Order);
        Assert.Equal(RecordStatus.Verified, (await _workflow.LoadRecordAsync("user-a", key))!.Record.Status);
    }

    [Fact]
    public async Task Confirming_requires_complete_billing_details()
    {
        var key = await ImportAsync();
        await _workflow.GenerateFormAsync("user-a", key);
        await _workflow.SaveFormAsync("user-a", key, new Dictionary<string, string?> { ["OwnerName"] = "Maria Lopez" }, "Alice Adams");

        var error = await Assert.ThrowsAsync<WorkflowException>(() =>
            _workflow.ConfirmAndPayAsync("user-a", key, PaymentMethodKind.Card, "Alice Adams", Urls));
        Assert.Contains("Billing email", error.Message);
        Assert.Empty(_db.Orders);
    }

    [Fact]
    public async Task Recipient_must_complete_required_fields_and_use_valid_formats()
    {
        var key = await ImportAsync();
        await _workflow.GenerateFormAsync("user-a", key);
        var link = await _workflow.GetOrCreateShareLinkAsync("user-a", key, false);

        var result = await _workflow.SubmitRecipientAsync(link.Token,
            new Dictionary<string, string?> { ["OwnerName"] = "", ["StorePhone"] = "not a phone", [FormBuilder.BillingZip] = "ABC" },
            new Dictionary<string, string?>(), null, k => k);

        Assert.Equal(RecipientSubmitOutcome.ValidationFailed, result.Outcome);
        Assert.Contains("OwnerName", result.Errors.Keys);
        Assert.Contains("StorePhone", result.Errors.Keys);
        Assert.Contains(FormBuilder.BillingZip, result.Errors.Keys);
        Assert.NotNull(await _workflow.GetSharedFormAsync(link.Token, false)); // link still usable after a failed attempt
    }

    [Fact]
    public async Task Opening_a_record_starts_one_call_and_call_again_adds_another()
    {
        var key = await ImportAsync();

        var first = (await _workflow.StartCallAsync("user-a", key)).Record;
        Assert.Single(first.Calls);
        Assert.True(first.IsOnCall);
        Assert.Single((await _workflow.StartCallAsync("user-a", key)).Record.Calls);              // reopening doesn't add a call
        Assert.Single((await _workflow.StartCallAsync("user-a", key, again: true)).Record.Calls); // still on the first call

        var ended = await _workflow.EndCallAsync("user-a", key);
        Assert.False(ended.Record.IsOnCall);
        Assert.NotNull(ended.Record.LastCall!.Duration);
        Assert.Contains("Store Name", ended.Workspace.Headers);
        Assert.Single((await _workflow.StartCallAsync("user-a", key)).Record.Calls);              // opening again just shows it

        var again = (await _workflow.StartCallAsync("user-a", key, again: true)).Record;
        Assert.Equal(2, again.Calls.Count);
        Assert.True(again.IsOnCall);
    }

    [Fact]
    public async Task Outcome_and_notes_are_saved_on_the_latest_call()
    {
        var key = await ImportAsync();
        await Assert.ThrowsAsync<WorkflowException>(() =>                  // no call yet
            _workflow.SetCallOutcomeAsync("user-a", key, CallOutcome.Interested, null));

        await _workflow.StartCallAsync("user-a", key);
        var first = await _workflow.SetCallOutcomeAsync("user-a", key, CallOutcome.CallBack, "  Owner busy, call after 5 PM  ");
        Assert.Equal(CallOutcome.CallBack, first.Record.LastCall!.Outcome);
        Assert.Equal("Owner busy, call after 5 PM", first.Record.LastCall.Notes);
        Assert.True(first.Record.LastCall.NeedsFollowUp);

        await _workflow.EndCallAsync("user-a", key);
        await _workflow.StartCallAsync("user-a", key, again: true);
        var second = await _workflow.SetCallOutcomeAsync("user-a", key, CallOutcome.Interested, null);

        Assert.Equal(CallOutcome.CallBack, second.Record.Calls[0].Outcome);   // earlier call keeps its outcome
        Assert.Equal(CallOutcome.Interested, second.Record.Calls[1].Outcome);
        Assert.Null(second.Record.Calls[1].Notes);

        await Assert.ThrowsAsync<WorkflowException>(() =>
            _workflow.SetCallOutcomeAsync("user-a", key, null, new string('x', CallEntry.MaxNotesLength + 1)));
        await Assert.ThrowsAsync<WorkflowException>(() =>
            _workflow.SetCallOutcomeAsync("user-a", key, (CallOutcome)99, null));
    }

    [Fact]
    public async Task Calls_and_notes_are_saved_to_the_database_as_they_happen()
    {
        var key = await ImportAsync();

        var started = await _workflow.StartCallAsync("user-a", key);
        var log = await _db.CallLogs.AsNoTracking().SingleAsync();
        Assert.Equal(started.Record.LastCall!.Id, log.PublicId);
        Assert.Equal(("user-a", "C-1", "Main Street Market", "stores.csv"), (log.UserId, log.ContactId, log.StoreName, log.SourceFile));
        Assert.Null(log.EndedAtUtc);

        await _workflow.SetCallOutcomeAsync("user-a", key, CallOutcome.CallBack, "Call after 5 PM");
        await _workflow.EndCallAsync("user-a", key);
        log = await _db.CallLogs.AsNoTracking().SingleAsync();
        Assert.Equal(CallOutcome.CallBack, log.Outcome);
        Assert.Equal("Call after 5 PM", log.Notes);
        Assert.NotNull(log.EndedAtUtc);

        await _workflow.StartCallAsync("user-a", key, again: true);
        Assert.Equal(2, await _db.CallLogs.CountAsync());
    }

    [Fact]
    public async Task Call_history_comes_back_when_the_list_is_uploaded_again()
    {
        var key = await ImportAsync();
        await _workflow.StartCallAsync("user-a", key);
        await _workflow.SetCallOutcomeAsync("user-a", key, CallOutcome.Interested, "Wants the form by email");

        await _workflow.EndSessionAsync("user-a", "test");                 // open call is closed in the log
        Assert.NotNull((await _db.CallLogs.AsNoTracking().SingleAsync()).EndedAtUtc);

        Assert.Null(await _workflow.GetWorkspaceAsync("user-a"));
        var newKey = await ImportAsync();
        var reloaded = (await _workflow.LoadRecordAsync("user-a", newKey))!.Record;
        Assert.Single(reloaded.Calls);
        Assert.False(reloaded.IsOnCall);
        Assert.Equal(CallOutcome.Interested, reloaded.LastCall!.Outcome);
        Assert.Equal("Wants the form by email", reloaded.LastCall.Notes);

        // Another caller uploading the same list doesn't see these calls.
        await _workflow.ImportCsvAsync("user-b", new MemoryStream(Encoding.UTF8.GetBytes(Csv)), "stores.csv");
        Assert.All((await _workflow.GetWorkspaceAsync("user-b"))!.Records, r => Assert.Empty(r.Calls));
    }

    [Fact]
    public async Task Starting_a_call_ends_the_call_on_another_store()
    {
        var first = await ImportAsync();
        var second = (await _workflow.GetWorkspaceAsync("user-a"))!.Records.Single(r => r.ContactId == "C-2").Key;

        await _workflow.StartCallAsync("user-a", first);
        var ctx = await _workflow.StartCallAsync("user-a", second);

        Assert.False(ctx.Workspace.FindRecord(first)!.IsOnCall);
        Assert.True(ctx.Workspace.FindRecord(second)!.IsOnCall);
    }

    [Fact]
    public async Task Recipient_chooses_the_payment_method()
    {
        var key = await ImportAndCompleteFormAsync();
        var link = await _workflow.GetOrCreateShareLinkAsync("user-a", key, false);
        var method = (await _workflow.LoadRecordAsync("user-a", key))!.Record.FindField(FormBuilder.BillingMethod)!;
        Assert.Equal(PaymentMethodKind.Card.Name(), method.Value);
        Assert.True(method.RecipientEditable);

        var values = new Dictionary<string, string?>(Billing) { ["OwnerName"] = "Maria Lopez", [FormBuilder.BillingMethod] = PaymentMethodKind.BankAccount.Name() };
        var submit = await _workflow.SubmitRecipientAsync(link.Token, values, new Dictionary<string, string?>(), "Maria", k => k);
        Assert.Equal(RecipientSubmitOutcome.Submitted, submit.Outcome);

        var record = (await _workflow.LoadRecordAsync("user-a", key))!.Record;
        Assert.Equal(PaymentMethodKind.BankAccount, FormBuilder.ChosenPaymentMethod(record));
    }

    [Fact]
    public async Task Other_users_cannot_reach_a_record_even_with_its_key()
    {
        var key = await ImportAsync("user-a");

        await Assert.ThrowsAsync<WorkspaceExpiredException>(() => _workflow.LoadRecordAsync("user-b", key));

        await _workflow.ImportCsvAsync("user-b", new MemoryStream(Encoding.UTF8.GetBytes(Csv)), "stores.csv");
        Assert.Null(await _workflow.LoadRecordAsync("user-b", key));
        await Assert.ThrowsAsync<WorkflowException>(() => _workflow.SetResponseAsync("user-b", key, "Y"));
        await Assert.ThrowsAsync<WorkflowException>(() => _workflow.StartCallAsync("user-b", key, again: false));
    }

    [Fact]
    public async Task Regenerating_a_share_link_revokes_the_old_one()
    {
        var key = await ImportAsync();
        await _workflow.GenerateFormAsync("user-a", key);
        var first = await _workflow.GetOrCreateShareLinkAsync("user-a", key, false);
        var second = await _workflow.GetOrCreateShareLinkAsync("user-a", key, regenerate: true);

        Assert.NotEqual(first.Token, second.Token);
        Assert.Null(await _workflow.GetSharedFormAsync(first.Token, false));
        Assert.NotNull(await _workflow.GetSharedFormAsync(second.Token, false));
    }

    [Fact]
    public async Task Confirming_stops_the_share_link()
    {
        var key = await ImportAndCompleteFormAsync();
        var link = await _workflow.GetOrCreateShareLinkAsync("user-a", key, false);

        await _workflow.ConfirmAndPayAsync("user-a", key, PaymentMethodKind.Card, "Alice Adams", Urls);

        Assert.Null(await _workflow.GetSharedFormAsync(link.Token, false));
    }

    [Fact]
    public async Task Ending_the_session_invalidates_share_links()
    {
        var key = await ImportAsync();
        await _workflow.GenerateFormAsync("user-a", key);
        var link = await _workflow.GetOrCreateShareLinkAsync("user-a", key, false);

        await _workflow.EndSessionAsync("user-a", "test");

        Assert.Null(await _workflow.GetWorkspaceAsync("user-a"));
        Assert.Null(await _workflow.GetSharedFormAsync(link.Token, false));
    }

    [Fact]
    public async Task Reuploading_after_session_loss_completes_a_paid_order_without_charging_again()
    {
        var key = await ImportAndCompleteFormAsync();
        var confirm = await _workflow.ConfirmAndPayAsync("user-a", key, PaymentMethodKind.Card, "Alice Adams", Urls);
        await _workflow.EndSessionAsync("user-a", "test");                 // session lost while on the payment page
        var paid = await PayAsync("user-a", confirm.Order);
        Assert.Equal(OrderStatus.Paid, paid.Status);
        Assert.Empty(_db.FinalizedRecords);

        var import = await _workflow.ImportCsvAsync("user-a", new MemoryStream(Encoding.UTF8.GetBytes(Csv)), "stores.csv");
        Assert.Contains(import.Warnings, w => w.Contains("already paid"));
        var record = (await _workflow.GetWorkspaceAsync("user-a"))!.Records.Single(r => r.ContactId == "C-1");
        Assert.Equal(paid.PublicId, record.OrderPublicId);

        await _workflow.GenerateFormAsync("user-a", record.Key);
        await _workflow.SaveFormAsync("user-a", record.Key, new Dictionary<string, string?>(Billing) { ["OwnerName"] = "Maria Lopez" }, "Alice Adams");
        var again = await _workflow.ConfirmAndPayAsync("user-a", record.Key, PaymentMethodKind.Card, "Alice Adams", Urls);

        Assert.Null(again.PaymentUrl);
        Assert.NotNull(again.Finalized);
        Assert.Equal(paid.Id, again.Order.Id);
        Assert.Single(_db.Orders);
    }

    private sealed class NoopAudit : IAuditLogger
    {
        public Task LogAsync(string category, string action, string? details = null, string? entityType = null, string? entityId = null,
            bool succeeded = true, string? userId = null, string? userName = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapturingEmailSender : IAppEmailSender
    {
        public List<(string To, string Subject)> Sent { get; } = [];

        public Task SendAsync(string toAddress, string subject, string htmlBody, string textBody, CancellationToken ct = default)
        {
            Sent.Add((toAddress, subject));
            return Task.CompletedTask;
        }
    }
}
