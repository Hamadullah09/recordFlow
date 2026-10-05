using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RecordFlow.Core.Abstractions;
using RecordFlow.Core.Workspaces;

namespace RecordFlow.Infrastructure.Workspaces;

/// <summary>
/// Stores workspaces in <see cref="IDistributedCache"/> (in-memory or Redis), encrypted with ASP.NET Core
/// Data Protection and expiring automatically (sliding idle timeout + absolute lifetime). Nothing here
/// touches the permanent SQL Server database.
///
/// Cache layout:
///   rf:ws:{workspaceId}   → encrypted workspace JSON
///   rf:wsu:{userId}       → workspaceId of the user's single active workspace
///   rf:share:{tokenHash}  → "{workspaceId}|{recordKey}" (only the SHA-256 of share tokens is used as a key)
/// </summary>
public sealed class DistributedWorkspaceStore : IWorkspaceStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    // Striped locks serialize concurrent updates to the same workspace (user + recipient) within a process.
    private static readonly SemaphoreSlim[] Locks = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private readonly IDistributedCache _cache;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;
    private readonly ILogger<DistributedWorkspaceStore> _logger;

    public DistributedWorkspaceStore(
        IDistributedCache cache,
        IDataProtectionProvider dataProtection,
        IOptions<WorkspaceOptions> options,
        TimeProvider clock,
        ILogger<DistributedWorkspaceStore> logger)
    {
        _cache = cache;
        _protector = dataProtection.CreateProtector("RecordFlow.Workspace.v1");
        _clock = clock;
        _logger = logger;
        IdleTimeout = TimeSpan.FromMinutes(Math.Max(5, options.Value.IdleTimeoutMinutes));
        MaxLifetime = TimeSpan.FromHours(Math.Max(1, options.Value.MaxLifetimeHours));
    }

    public TimeSpan IdleTimeout { get; }
    public TimeSpan MaxLifetime { get; }

    public async Task<Workspace?> GetForUserAsync(string userId, CancellationToken ct = default)
    {
        var id = await _cache.GetStringAsync(UserKey(userId), ct);
        if (id is null) return null;
        var ws = await LoadAsync(id, ct);
        return ws is not null && ws.OwnerUserId == userId ? ws : null;
    }

    public Task<Workspace?> GetByIdAsync(string workspaceId, CancellationToken ct = default) => LoadAsync(workspaceId, ct);

    public async Task SaveNewAsync(Workspace workspace, CancellationToken ct = default)
    {
        await DeleteForUserAsync(workspace.OwnerUserId, ct);
        await SaveAsync(workspace, ct);
        await _cache.SetStringAsync(UserKey(workspace.OwnerUserId), workspace.Id, EntryOptions(workspace), ct);
    }

    public async Task<T> UpdateForUserAsync<T>(string userId, Func<Workspace, T> mutate, CancellationToken ct = default)
    {
        var id = await _cache.GetStringAsync(UserKey(userId), ct) ?? throw new WorkspaceExpiredException();
        return await UpdateCoreAsync(id, ws => ws.OwnerUserId == userId, mutate, ct);
    }

    public async Task<T> UpdateByIdAsync<T>(string workspaceId, Func<Workspace, T> mutate, CancellationToken ct = default)
    {
        var result = await UpdateCoreAsync(workspaceId, _ => true, mutate, ct);
        return result;
    }

    public async Task DeleteForUserAsync(string userId, CancellationToken ct = default)
    {
        var id = await _cache.GetStringAsync(UserKey(userId), ct);
        if (id is not null)
        {
            var gate = LockFor(id);
            await gate.WaitAsync(ct);
            try
            {
                var ws = await LoadAsync(id, ct);
                if (ws is not null)
                {
                    foreach (var share in ws.Records.Select(r => r.Share).OfType<ShareInfo>())
                        await _cache.RemoveAsync(ShareKey(share.TokenHash), ct);
                }
                await _cache.RemoveAsync(WorkspaceKey(id), ct);
            }
            finally
            {
                gate.Release();
            }
        }
        await _cache.RemoveAsync(UserKey(userId), ct);
    }

    public Task RegisterShareTokenAsync(string tokenHash, string workspaceId, string recordKey, DateTimeOffset expiresAtUtc, CancellationToken ct = default) =>
        _cache.SetStringAsync(ShareKey(tokenHash), $"{workspaceId}|{recordKey}",
            new DistributedCacheEntryOptions { AbsoluteExpiration = expiresAtUtc }, ct);

    public async Task<SharedRecordPointer?> ResolveShareTokenAsync(string tokenHash, CancellationToken ct = default)
    {
        var value = await _cache.GetStringAsync(ShareKey(tokenHash), ct);
        var parts = value?.Split('|');
        return parts is { Length: 2 } ? new SharedRecordPointer(parts[0], parts[1]) : null;
    }

    public Task RemoveShareTokenAsync(string tokenHash, CancellationToken ct = default) =>
        _cache.RemoveAsync(ShareKey(tokenHash), ct);

    private async Task<T> UpdateCoreAsync<T>(string id, Func<Workspace, bool> authorize, Func<Workspace, T> mutate, CancellationToken ct)
    {
        var gate = LockFor(id);
        await gate.WaitAsync(ct);
        try
        {
            var ws = await LoadAsync(id, ct);
            if (ws is null || !authorize(ws)) throw new WorkspaceExpiredException();

            var result = mutate(ws);
            await SaveAsync(ws, ct);
            // Keep the owner's pointer alive while anyone (e.g. the recipient) is actively using the workspace.
            await _cache.RefreshAsync(UserKey(ws.OwnerUserId), ct);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Workspace?> LoadAsync(string id, CancellationToken ct)
    {
        var payload = await _cache.GetAsync(WorkspaceKey(id), ct);
        if (payload is null) return null;
        try
        {
            var ws = JsonSerializer.Deserialize<Workspace>(_protector.Unprotect(payload), Json);
            if (ws is null || ws.AbsoluteExpiresAtUtc <= _clock.GetUtcNow()) return null;
            return ws;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            _logger.LogWarning(ex, "Discarding unreadable workspace {WorkspaceId}.", id);
            await _cache.RemoveAsync(WorkspaceKey(id), ct);
            return null;
        }
    }

    private async Task SaveAsync(Workspace ws, CancellationToken ct)
    {
        if (ws.AbsoluteExpiresAtUtc <= _clock.GetUtcNow()) throw new WorkspaceExpiredException();
        var payload = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(ws, Json));
        await _cache.SetAsync(WorkspaceKey(ws.Id), payload, EntryOptions(ws), ct);
    }

    private DistributedCacheEntryOptions EntryOptions(Workspace ws) => new()
    {
        SlidingExpiration = IdleTimeout,
        AbsoluteExpiration = ws.AbsoluteExpiresAtUtc,
    };

    private static SemaphoreSlim LockFor(string id) =>
        Locks[(uint)StringComparer.Ordinal.GetHashCode(id) % (uint)Locks.Length];

    private static string WorkspaceKey(string id) => $"rf:ws:{id}";
    private static string UserKey(string userId) => $"rf:wsu:{userId}";
    private static string ShareKey(string hash) => $"rf:share:{hash}";
}
