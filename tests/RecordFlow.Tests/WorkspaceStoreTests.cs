using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Workspaces;
using RecordFlow.Infrastructure.Workspaces;

namespace RecordFlow.Tests;

public class WorkspaceStoreTests
{
    private readonly MemoryDistributedCache _cache = new(Options.Create(new MemoryDistributedCacheOptions()));
    private readonly DistributedWorkspaceStore _store;

    public WorkspaceStoreTests()
    {
        _store = new DistributedWorkspaceStore(_cache, new EphemeralDataProtectionProvider(),
            Options.Create(new WorkspaceOptions { IdleTimeoutMinutes = 30, MaxLifetimeHours = 2 }),
            TimeProvider.System, NullLogger<DistributedWorkspaceStore>.Instance);
    }

    private static Workspace NewWorkspace(string owner, string id, string secretValue = "Secret Store") => new()
    {
        Id = id,
        OwnerUserId = owner,
        FileName = "stores.csv",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        AbsoluteExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
        Headers = ["Contact ID", "Store Name"],
        Records = [new WorkingRecord { Key = "rec-" + id, ContactId = "C-1", CsvValues = new() { ["Store Name"] = secretValue } }],
    };

    [Fact]
    public async Task Users_cannot_see_or_modify_each_others_workspaces()
    {
        await _store.SaveNewAsync(NewWorkspace("user-a", "ws-a"));
        await _store.SaveNewAsync(NewWorkspace("user-b", "ws-b"));

        var a = await _store.GetForUserAsync("user-a");
        var b = await _store.GetForUserAsync("user-b");
        Assert.Equal("ws-a", a!.Id);
        Assert.Equal("ws-b", b!.Id);
        Assert.Null(a.FindRecord("rec-ws-b"));                       // B's record key is meaningless in A's workspace
        Assert.Null(await _store.GetForUserAsync("user-c"));
        await Assert.ThrowsAsync<WorkspaceExpiredException>(() => _store.UpdateForUserAsync("user-c", w => 0));
    }

    [Fact]
    public async Task Workspace_data_is_encrypted_in_the_cache()
    {
        await _store.SaveNewAsync(NewWorkspace("user-a", "ws-a", secretValue: "Very Private Grocery"));

        var raw = await _cache.GetAsync("rf:ws:ws-a");
        Assert.NotNull(raw);
        Assert.DoesNotContain("Very Private Grocery", Encoding.UTF8.GetString(raw!));
        Assert.DoesNotContain("Very Private Grocery", Encoding.Unicode.GetString(raw!));
    }

    [Fact]
    public async Task Uploading_a_new_csv_replaces_the_old_workspace_and_its_share_links()
    {
        var first = NewWorkspace("user-a", "ws-1");
        first.Records[0].Share = new ShareInfo { TokenHash = "HASH1", ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) };
        await _store.SaveNewAsync(first);
        await _store.RegisterShareTokenAsync("HASH1", "ws-1", "rec-ws-1", DateTimeOffset.UtcNow.AddHours(1));

        await _store.SaveNewAsync(NewWorkspace("user-a", "ws-2"));

        Assert.Equal("ws-2", (await _store.GetForUserAsync("user-a"))!.Id);
        Assert.Null(await _store.GetByIdAsync("ws-1"));
        Assert.Null(await _store.ResolveShareTokenAsync("HASH1"));
    }

    [Fact]
    public async Task Ending_the_session_deletes_all_temporary_data()
    {
        await _store.SaveNewAsync(NewWorkspace("user-a", "ws-a"));
        await _store.DeleteForUserAsync("user-a");

        Assert.Null(await _store.GetForUserAsync("user-a"));
        Assert.Null(await _cache.GetAsync("rf:ws:ws-a"));
        Assert.Null(await _cache.GetAsync("rf:wsu:user-a"));
    }

    [Fact]
    public async Task Updates_are_persisted()
    {
        await _store.SaveNewAsync(NewWorkspace("user-a", "ws-a"));
        await _store.UpdateForUserAsync("user-a", w => w.Records[0].Response = "Y");
        Assert.Equal("Y", (await _store.GetForUserAsync("user-a"))!.Records[0].Response);
    }
}
