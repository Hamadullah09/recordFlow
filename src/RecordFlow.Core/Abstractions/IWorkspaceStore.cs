using RecordFlow.Core.Workspaces;

namespace RecordFlow.Core.Abstractions;

/// <summary>
/// Temporary, per-user, auto-expiring storage for CSV working sessions. Implementations must
/// keep data out of the permanent database, encrypt it at rest and isolate users from each other.
/// </summary>
public interface IWorkspaceStore
{
    TimeSpan IdleTimeout { get; }
    TimeSpan MaxLifetime { get; }

    Task<Workspace?> GetForUserAsync(string userId, CancellationToken ct = default);

    /// <summary>Stores a new workspace for its owner, discarding any previous workspace (and its share links).</summary>
    Task SaveNewAsync(Workspace workspace, CancellationToken ct = default);

    /// <summary>
    /// Applies <paramref name="mutate"/> under a per-workspace lock and saves the result.
    /// Throws <see cref="WorkspaceExpiredException"/> when the user has no active workspace.
    /// </summary>
    Task<T> UpdateForUserAsync<T>(string userId, Func<Workspace, T> mutate, CancellationToken ct = default);

    /// <summary>Same as <see cref="UpdateForUserAsync{T}"/> but addressed by workspace id (recipient flow).</summary>
    Task<T> UpdateByIdAsync<T>(string workspaceId, Func<Workspace, T> mutate, CancellationToken ct = default);

    Task<Workspace?> GetByIdAsync(string workspaceId, CancellationToken ct = default);

    Task DeleteForUserAsync(string userId, CancellationToken ct = default);

    Task RegisterShareTokenAsync(string tokenHash, string workspaceId, string recordKey, DateTimeOffset expiresAtUtc, CancellationToken ct = default);
    Task<SharedRecordPointer?> ResolveShareTokenAsync(string tokenHash, CancellationToken ct = default);
    Task RemoveShareTokenAsync(string tokenHash, CancellationToken ct = default);
}

public sealed record SharedRecordPointer(string WorkspaceId, string RecordKey);

public sealed class WorkspaceExpiredException() : Exception("The working session has expired or does not exist.");

public sealed class WorkflowException(string message) : Exception(message);
