using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     Acquires and renews the single-drainer lease (<see cref="SealedFgaOutboxLease" />) via one
///     atomic conditional UPDATE — the database row lock serializes competing replicas, so no
///     transaction or concurrency token is needed. Expiry comparisons use the caller's clock
///     against the stored expiry; the lease duration must therefore comfortably exceed the
///     renewal cadence (one drain pass) plus expected inter-replica clock skew.
/// </summary>
internal static class SealedFgaOutboxLeaseManager {
    internal const string LeaseName = "outbox-drainer";

    /// <summary>
    ///     Tries to acquire the lease, or renew it when <paramref name="holderId" /> already holds
    ///     it. Returns whether the caller is the active drainer. Bootstraps the lease row on first
    ///     use (a concurrent-insert race is resolved by the losing replica retrying the UPDATE).
    /// </summary>
    internal static async Task<bool> TryAcquireOrRenewAsync(
        DbContext context,
        string holderId,
        TimeSpan leaseDuration,
        TimeProvider timeProvider,
        CancellationToken ct = default
    ) {
        if (await TryUpdateLeaseAsync(context, holderId, leaseDuration, timeProvider, ct)) {
            return true;
        }

        var set = context.Set<SealedFgaOutboxLease>();
        if (await set.AsNoTracking().AnyAsync(l => l.Name == LeaseName, ct)) {
            return false; // Row exists and is validly held by someone else.
        }

        // First use on this database: create the row already expired, then compete for it.
        var lease = new SealedFgaOutboxLease {
            Name = LeaseName,
            HolderId = null,
            ExpiresAtUtc = DateTime.UnixEpoch, // Kind=Utc; DateTime.MinValue is Unspecified and Npgsql rejects it.
        };
        set.Add(lease);
        try {
            await context.SaveChangesAsync(ct);
        } catch (DbUpdateException) {
            // Another replica inserted it first; fall through and compete for the existing row.
        } finally {
            context.Entry(lease).State = EntityState.Detached;
        }

        return await TryUpdateLeaseAsync(context, holderId, leaseDuration, timeProvider, ct);
    }

    private static async Task<bool> TryUpdateLeaseAsync(
        DbContext context,
        string holderId,
        TimeSpan leaseDuration,
        TimeProvider timeProvider,
        CancellationToken ct
    ) {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expiresAt = now.Add(leaseDuration);
        var affected = await context.Set<SealedFgaOutboxLease>()
                                    .Where(l => l.Name == LeaseName
                                                && (l.ExpiresAtUtc < now || l.HolderId == holderId)
                                     )
                                    .ExecuteUpdateAsync(setters => setters
                                            .SetProperty(l => l.HolderId, holderId)
                                            .SetProperty(l => l.ExpiresAtUtc, expiresAt),
                                        ct
                                     );
        return affected == 1;
    }
}
