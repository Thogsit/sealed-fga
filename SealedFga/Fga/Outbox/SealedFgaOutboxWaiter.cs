using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     Waits until the outbox rows enqueued in this scope have been applied to OpenFGA, giving the
///     caller read-after-write: once this returns, a permission check on the entity that was just
///     created will succeed.
///     <para>
///         <b>It never drains.</b> Applying rows stays the exclusive job of the replica holding the
///         drainer lease, because the drainer's newest-wins and fence rules are only sound with a
///         single active applier. This type only observes <c>ProcessedAtUtc</c>, so it is safe to
///         call from any number of concurrent requests, on any replica.
///     </para>
///     <para>
///         The wait is therefore only as fast as the drainer is woken. In-process enqueues are woken
///         immediately by <see cref="SealedFgaOutboxSignal" />; enqueues on a replica that does not
///         hold the lease need a cross-replica notifier (see <c>SealedFga.Npgsql</c>) or they fall
///         back to <see cref="SealedFgaOptions.OutboxPollInterval" />.
///     </para>
/// </summary>
/// <typeparam name="TDbContext">The consumer's <see cref="DbContext" /> that hosts the outbox table.</typeparam>
/// <param name="context">The scope's context, used only to read row state.</param>
/// <param name="pendingRows">The rows this scope enqueued.</param>
/// <param name="options">Timeout, poll cadence and the drainer-enabled flag.</param>
public class SealedFgaOutboxWaiter<TDbContext>(
    TDbContext context,
    SealedFgaOutboxPendingRows pendingRows,
    IOptions<SealedFgaOptions> options
) where TDbContext : DbContext {
    /// <summary>
    ///     Waits for everything enqueued in this scope so far, clearing it so a later call only
    ///     covers subsequent work.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    ///     <c>true</c> when the rows were applied (or there were none to wait for); <c>false</c> when
    ///     the timeout lapsed first, or the scope enqueued more rows than
    ///     <see cref="SealedFgaOptions.OutboxWaitMaxRows" />. A <c>false</c> is not an error — the
    ///     rows remain queued and the drainer will still apply them.
    /// </returns>
    public async Task<bool> WaitForPendingAsync(CancellationToken cancellationToken = default) {
        var (rowIds, overflowed) = pendingRows.Take();
        if (overflowed) {
            return false;
        }

        return await WaitForAsync(rowIds, cancellationToken);
    }

    /// <summary>
    ///     Waits for a specific set of outbox rows — for callers that captured ids themselves rather
    ///     than relying on scope tracking.
    /// </summary>
    /// <param name="rowIds">The outbox row ids to wait for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when every row is applied; <c>false</c> on timeout.</returns>
    public async Task<bool> WaitForAsync(
        IReadOnlyList<long> rowIds,
        CancellationToken cancellationToken = default
    ) {
        var opts = options.Value;
        if (rowIds.Count == 0) {
            return true;
        }

        // With no drainer in the deployment nothing will ever mark these rows processed, so waiting
        // would burn the full timeout on every single call. Consumers that drive the drainer
        // themselves (tests, a dedicated worker process) opt back in by awaiting their own drain.
        if (!opts.RunOutboxDrainer) {
            return true;
        }

        var ids = rowIds.ToArray();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(opts.OutboxWaitTimeout);

        try {
            while (true) {
                if (!await AnyUnprocessedAsync(ids, timeout.Token)) {
                    return true;
                }

                await Task.Delay(opts.OutboxWaitPollInterval, timeout.Token);
            }
        } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            // Our own timeout, not the caller's cancellation: the write succeeded and the rows are
            // still queued, so report the miss rather than failing the caller's operation.
            return false;
        }
    }

    /// <summary>
    ///     Waits for the whole outbox backlog as it stands right now — every row already enqueued,
    ///     whoever enqueued it — rather than only this scope's rows.
    ///     <para>
    ///         This is the one to use when the work you depend on was committed by <b>someone else</b>:
    ///         a queued background task acting on tuple changes an earlier request enqueued has
    ///         nothing of its own to wait for, so <see cref="WaitForPendingAsync" /> would return
    ///         instantly and read stale authorization state. Prefer the scope-local wait everywhere
    ///         else — this one's latency is whatever the current backlog costs.
    ///     </para>
    ///     <para>
    ///         Rows that have exhausted <see cref="SealedFgaOptions.OutboxMaxAttempts" /> are excluded:
    ///         nothing will ever apply them, so waiting on them could only ever time out. Their tuples
    ///         are genuinely missing, which is what the outbox health check is for.
    ///     </para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when the backlog is drained; <c>false</c> on timeout.</returns>
    public async Task<bool> WaitForBacklogAsync(CancellationToken cancellationToken = default) {
        var opts = options.Value;
        if (!opts.RunOutboxDrainer) {
            return true;
        }

        // Snapshot the backlog: rows enqueued after this point are someone else's concern, and
        // waiting for them too could keep a busy system's caller here indefinitely.
        var watermark = await context.Set<SealedFgaOutboxEntry>()
                                     .AsNoTracking()
                                     .Where(e => e.ProcessedAtUtc == null)
                                     .MaxAsync(e => (long?) e.Id, cancellationToken);
        if (watermark is null) {
            return true;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(opts.OutboxWaitTimeout);

        try {
            while (true) {
                var stillPending = await context.Set<SealedFgaOutboxEntry>()
                                                .AsNoTracking()
                                                .AnyAsync(
                                                     e => e.Id <= watermark
                                                          && e.ProcessedAtUtc == null
                                                          && e.Attempts < opts.OutboxMaxAttempts,
                                                     timeout.Token
                                                 );
                if (!stillPending) {
                    return true;
                }

                await Task.Delay(opts.OutboxWaitPollInterval, timeout.Token);
            }
        } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            return false;
        }
    }

    /// <summary>
    ///     Reads row state on a fresh, untracked query — the scope's context may still be tracking
    ///     these very rows from the save that created them, and their cached state is stale by
    ///     definition here.
    /// </summary>
    private async Task<bool> AnyUnprocessedAsync(long[] ids, CancellationToken ct) =>
        await context.Set<SealedFgaOutboxEntry>()
                     .AsNoTracking()
                     .AnyAsync(e => ids.Contains(e.Id) && e.ProcessedAtUtc == null, ct);
}
