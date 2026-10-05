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
/// upload → checkout → payment → form → share → recipient → verify/confirm.
/// </summary>
public class WorkflowIntegrationTests : IDisposable
{
    private const string Csv =
        "Contact ID,Store Name,Address Line 1,City,State,ZIP Code,Store Phone Number,Owner Name,Region\n" +
        "C-1,Main Street Market,125 Main St,Springfield,IL,62701,(217) 555-0142,,Midwest\n" +
        "C-2,Harbor Grocery,48 Harbor Rd,Portland,ME,04101,,Ann Lee,Northeast\n";

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
            Options.Create(new AppOptions()), TimeProvider.System, NullLogger<RecordWorkflowService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _memory.Dispose();
    }

    private async Task<(string Key, Order Order)> ImportAndPayAsync(string userId = "user-a", string contactId = "C-1")
    {
        var import = await _workflow.ImportCsvAsync(userId, new MemoryStream(Encoding.UTF8.GetBytes(Csv)), "stores.csv");
        Assert.True(import.Succeeded);
        var key = (await _workflow.GetWorkspaceAsync(userId))!.Records.Single(r => r.ContactId == contactId).Key;

        var checkout = new CheckoutDetails { BillingName = "Alice Adams", BillingEmail = "alice@test.local", AddressLine1 = "1 Pay St", City = "Chicago", State = "IL", ZipCode = "60601" };
        await _workflow.StartCheckoutAsync(userId, key, checkout, _ => new PaymentUrls("https://x/return?session_id={CHECKOUT_SESSION_ID}", "https://x/cancel"));

        var order = await _db.Orders.SingleAsync(o => o.UserId == userId && o.ContactId == contactId && o.Status == OrderStatus.Pending);
        _payments.Complete(order.ProviderSessionId!, approve: true);
        var result = await _workflow.CompletePaymentReturnAsync(userId, order.ProviderSessionId!);
        Assert.Equal(OrderStatus.Paid, result.Order!.Status);
        Assert.Equal(key, result.RecordKey);
        return (key, result.Order);
    }

    [Fact]
    public async Task Full_workflow_from_upload_to_confirmation()
    {
        var (key, order) = await ImportAndPayAsync();
        Assert.Equal(49.00m, order.Total);

        await _workflow.GenerateFormAsync("user-a", key);
        var record = (await _workflow.LoadRecordAsync("user-a", key))!.Record;
        Assert.Equal(RecordStatus.FormGenerated, record.Status);
        Assert.True(record.FindField("OwnerName")!.IsMissing);              // missing in CSV → empty, editable
        Assert.Equal("(217) 555-0142", record.FindField("StorePhone")!.Value);

        // Share, then the recipient completes the form.
        var link = await _workflow.GetOrCreateShareLinkAsync("user-a", key, regenerate: false);
        var shared = await _workflow.GetSharedFormAsync(link.Token, markOpened: true);
        Assert.NotNull(shared);
        Assert.Equal("Alice Adams", shared.Order!.BillingName);              // read-only order info is available to show

        var submit = await _workflow.SubmitRecipientAsync(link.Token,
            values: new Dictionary<string, string?>
            {
                ["OwnerName"] = "Maria Lopez",
                ["StorePhone"] = "(217) 555-0142",
                ["StoreName"] = "Hijacked Name",                             // not recipient-editable → ignored
                ["BillingName"] = "Hacker",                                 // not a form field at all → ignored
            },
            originals: new Dictionary<string, string?> { ["OwnerName"] = null, ["StorePhone"] = "(217) 555-0142" },
            recipientName: "Maria", buildRecordUrl: k => $"https://x/records/{k}/verify");
        Assert.Equal(RecipientSubmitOutcome.Submitted, submit.Outcome);

        record = (await _workflow.LoadRecordAsync("user-a", key))!.Record;
        Assert.Equal(RecordStatus.ReadyForVerification, record.Status);
        Assert.Equal("Maria Lopez", record.FindField("OwnerName")!.Value);
        Assert.Equal(FieldSource.Recipient, record.FindField("OwnerName")!.Source);
        Assert.Equal("Main Street Market", record.FindField("StoreName")!.Value);
        Assert.Equal(FieldSource.Csv, record.FindField("StorePhone")!.Source); // unchanged value isn't attributed to recipient
        Assert.Equal("Alice Adams", (await _db.Orders.SingleAsync(o => o.Id == order.Id)).BillingName);
        Assert.Single(_email.Sent, m => m.To == "alice@test.local");        // owner notified automatically

        // The link works only once.
        Assert.Null(await _workflow.GetSharedFormAsync(link.Token, markOpened: false));
        var again = await _workflow.SubmitRecipientAsync(link.Token, new Dictionary<string, string?>(), new Dictionary<string, string?>(), null, k => k);
        Assert.Equal(RecipientSubmitOutcome.LinkInvalid, again.Outcome);

        // Verify & confirm: only the final values become permanent; the temporary CSV data is purged.
        var finalized = await _workflow.ConfirmAsync("user-a", key, "Alice Adams");
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
    public async Task Recipient_must_complete_required_fields_and_use_valid_formats()
    {
        var (key, _) = await ImportAndPayAsync();
        await _workflow.GenerateFormAsync("user-a", key);
        var link = await _workflow.GetOrCreateShareLinkAsync("user-a", key, false);

        var result = await _workflow.SubmitRecipientAsync(link.Token,
            new Dictionary<string, string?> { ["OwnerName"] = "", ["StorePhone"] = "not a phone" },
            new Dictionary<string, string?>(), null, k => k);

        Assert.Equal(RecipientSubmitOutcome.ValidationFailed, result.Outcome);
        Assert.Contains("OwnerName", result.Errors.Keys);
        Assert.Contains("StorePhone", result.Errors.Keys);
        Assert.NotNull(await _workflow.GetSharedFormAsync(link.Token, false)); // link still usable after a failed attempt
    }

    [Fact]
    public async Task Other_users_cannot_reach_a_record_even_with_its_key()
    {
        var (key, _) = await ImportAndPayAsync("user-a");

        await Assert.ThrowsAsync<WorkspaceExpiredException>(() => _workflow.LoadRecordAsync("user-b", key));

        await _workflow.ImportCsvAsync("user-b", new MemoryStream(Encoding.UTF8.GetBytes(Csv)), "stores.csv");
        Assert.Null(await _workflow.LoadRecordAsync("user-b", key));
        await Assert.ThrowsAsync<WorkflowException>(() => _workflow.SetResponseAsync("user-b", key, "Y"));
    }

    [Fact]
    public async Task Form_cannot_be_generated_before_payment()
    {
        await _workflow.ImportCsvAsync("user-a", new MemoryStream(Encoding.UTF8.GetBytes(Csv)), "stores.csv");
        var key = (await _workflow.GetWorkspaceAsync("user-a"))!.Records[0].Key;
        await Assert.ThrowsAsync<WorkflowException>(() => _workflow.GenerateFormAsync("user-a", key));
    }

    [Fact]
    public async Task Regenerating_a_share_link_revokes_the_old_one()
    {
        var (key, _) = await ImportAndPayAsync();
        await _workflow.GenerateFormAsync("user-a", key);
        var first = await _workflow.GetOrCreateShareLinkAsync("user-a", key, false);
        var second = await _workflow.GetOrCreateShareLinkAsync("user-a", key, regenerate: true);

        Assert.NotEqual(first.Token, second.Token);
        Assert.Null(await _workflow.GetSharedFormAsync(first.Token, false));
        Assert.NotNull(await _workflow.GetSharedFormAsync(second.Token, false));
    }

    [Fact]
    public async Task Ending_the_session_invalidates_share_links()
    {
        var (key, _) = await ImportAndPayAsync();
        await _workflow.GenerateFormAsync("user-a", key);
        var link = await _workflow.GetOrCreateShareLinkAsync("user-a", key, false);

        await _workflow.EndSessionAsync("user-a", "test");

        Assert.Null(await _workflow.GetWorkspaceAsync("user-a"));
        Assert.Null(await _workflow.GetSharedFormAsync(link.Token, false));
    }

    [Fact]
    public async Task Reuploading_after_session_loss_relinks_a_paid_order()
    {
        var (_, order) = await ImportAndPayAsync();
        await _workflow.EndSessionAsync("user-a", "test");

        var import = await _workflow.ImportCsvAsync("user-a", new MemoryStream(Encoding.UTF8.GetBytes(Csv)), "stores.csv");
        var record = (await _workflow.GetWorkspaceAsync("user-a"))!.Records.Single(r => r.ContactId == "C-1");

        Assert.Equal(order.PublicId, record.OrderPublicId);
        Assert.Equal(RecordStatus.Paid, record.Status);
        Assert.Contains(import.Warnings, w => w.Contains("already paid"));
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
