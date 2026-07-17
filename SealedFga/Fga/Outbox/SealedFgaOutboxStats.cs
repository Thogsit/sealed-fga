using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     A point-in-time snapshot of the outbox's backlog, for programmatic in-process monitoring:
///     SealedFGA ships no HTTP endpoints — consumers decide what, if anything, to expose
///     (e.g. via their own health-check or metrics infrastructure).
/// </summary>
public sealed record SealedFgaOutboxStats {
    /// <summary>Unprocessed rows that will still be retried (eligible or backed off).</summary>
    public required int PendingCount { get; init; }

    /// <summary>
    ///     Unprocessed rows whose attempts are exhausted — permanently failed and no longer
    ///     retried. A non-zero value means the database and OpenFGA may have silently diverged;
    ///     this is the outbox's primary alert condition.
    /// </summary>
    public required int ParkedCount { get; init; }

    /// <summary>
    ///     Age of the oldest still-pending row (by enqueue time), or <c>null</c> when nothing is
    ///     pending. A growing value means the backlog is not draining (e.g. OpenFGA outage, or
    ///     rows blocked behind a parked fence) and propagation SLOs are at risk.
    /// </summary>
    public required TimeSpan? OldestPendingAge { get; init; }
}

/// <summary>Query helper computing <see cref="SealedFgaOutboxStats" /> from the outbox table.</summary>
public static class SealedFgaOutboxStatsExtensions {
    /// <summary>
    ///     Computes the current outbox backlog stats. Plain LINQ over the outbox table — works on
    ///     any provider, including InMemory.
    /// </summary>
    /// <param name="context">The DbContext hosting the outbox table.</param>
    /// <param name="maxAttempts">
    ///     The parked threshold — pass the configured <see cref="SealedFgaOptions.OutboxMaxAttempts" />.
    /// </param>
    /// <param name="timeProvider">Clock for the age computation; defaults to the system clock.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<SealedFgaOutboxStats> GetSealedFgaOutboxStatsAsync(
        this DbContext context,
        int maxAttempts,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default
    ) {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var set = context.Set<SealedFgaOutboxEntry>().AsNoTracking();

        var pendingCount = await set.CountAsync(
            e => e.ProcessedAtUtc == null && e.Attempts < maxAttempts,
            ct
        );
        var parkedCount = await set.CountAsync(
            e => e.ProcessedAtUtc == null && e.Attempts >= maxAttempts,
            ct
        );
        var oldestPendingCreatedAt = pendingCount == 0
            ? null
            : await set.Where(e => e.ProcessedAtUtc == null && e.Attempts < maxAttempts)
                       .MinAsync(e => (DateTime?) e.CreatedAtUtc, ct);

        return new SealedFgaOutboxStats {
            PendingCount = pendingCount,
            ParkedCount = parkedCount,
            OldestPendingAge = oldestPendingCreatedAt == null ? null : now - oldestPendingCreatedAt,
        };
    }
}
