using System;
using System.Threading.Tasks;
using OpenFga.Sdk.Model;

namespace SealedFga.Fga;

/// <summary>
///     Process-wide cache for the OpenFGA authorization model (registered as a singleton;
///     <see cref="SealedFgaService" /> is scoped and shares this instance). Caching policy:
///     <list type="bullet">
///         <item>
///             Client pinned to an <c>AuthorizationModelId</c> → cached <b>indefinitely</b>
///             (models are immutable per ID).
///         </item>
///         <item>
///             Unpinned (server resolves the store's latest model) → cached with a TTL
///             (<see cref="SealedFgaOptions.AuthorizationModelCacheTtl" />), since "latest"
///             can move, e.g. on a redeploy that writes a new model.
///         </item>
///     </list>
/// </summary>
/// <param name="timeProvider">Optional clock override (tests); defaults to the system clock.</param>
public sealed class SealedFgaAuthModelCache(TimeProvider? timeProvider = null) {
    /// <summary>One immutable cache snapshot: what was fetched, under which pin, and when.</summary>
    private sealed record Snapshot(AuthorizationModel Model, string? PinnedModelId, DateTimeOffset FetchedAt);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private volatile Snapshot? _snapshot;

    /// <summary>
    ///     Returns the cached authorization model, fetching via <paramref name="fetchAsync" /> when
    ///     the cache is empty or stale. Lock-free: concurrent callers may fetch redundantly, but the
    ///     result converges (last write wins) and the model content is identical.
    /// </summary>
    /// <param name="pinnedModelId">
    ///     The client's pinned authorization model ID, or <c>null</c> when unpinned. A pin change
    ///     (or pinning/unpinning) invalidates the snapshot.
    /// </param>
    /// <param name="ttl">Maximum snapshot age while unpinned; ignored when pinned.</param>
    /// <param name="fetchAsync">Fetches the model from the server on a cache miss.</param>
    internal async Task<AuthorizationModel> GetModelAsync(
        string? pinnedModelId,
        TimeSpan ttl,
        Func<Task<AuthorizationModel>> fetchAsync
    ) {
        var snapshot = _snapshot;
        if (snapshot is not null && snapshot.PinnedModelId == pinnedModelId) {
            if (pinnedModelId is not null
                || _timeProvider.GetUtcNow() - snapshot.FetchedAt < ttl) {
                return snapshot.Model;
            }
        }

        var model = await fetchAsync();
        _snapshot = new Snapshot(model, pinnedModelId, _timeProvider.GetUtcNow());
        return model;
    }
}
