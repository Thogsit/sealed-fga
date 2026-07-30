using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     Base for interceptors that need to react to "a <c>SaveChanges</c> just completed and it
///     enqueued outbox rows" — the local wake-up (<see cref="SealedFgaOutboxSignalInterceptor" />)
///     and the cross-replica one (<see cref="SealedFgaOutboxNotifyInterceptor" />) both hang off
///     this, so the detection rules stay in one place and cannot drift apart.
///     <para>
///         Detection has to straddle two callbacks: the outbox rows are visible in the change tracker
///         during <c>SavingChanges</c> but gone by <c>SavedChanges</c> (they are committed and no
///         longer <see cref="EntityState.Added" />), while only <c>SavedChanges</c> knows the save
///         actually succeeded. The flag recorded on the way in is therefore read on the way out.
///     </para>
///     <para>
///         <b>Ordering:</b> the generated <c>AddSealedFga</c> registers these after
///         <c>SealedFgaSaveChangesInterceptor</c>, because the pending check has to run once that
///         interceptor has translated the tracked entity changes into outbox rows. Rows added
///         directly through the <c>EnqueueFga*</c> extensions are already in the change tracker by
///         then and are detected the same way.
///     </para>
/// </summary>
public abstract class SealedFgaOutboxCommitInterceptor : SaveChangesInterceptor {
    /// <summary>
    ///     The outbox rows the in-flight <c>SaveChanges</c> is inserting, keyed by the context it runs
    ///     on. Weak keys so a discarded context never keeps an entry alive; per-context because one
    ///     scope can hold several contexts and the async save/saved pair can hop threads.
    ///     Per-instance rather than static so two interceptors on the same context — or two
    ///     independent containers in a test — cannot consume each other's rows.
    ///     <para>
    ///         The entities are held rather than their ids because the ids are database-generated:
    ///         they are still 0 while the rows sit in the change tracker and only become readable once
    ///         the save has completed.
    ///     </para>
    /// </summary>
    private readonly ConditionalWeakTable<DbContext, List<SealedFgaOutboxEntry>> _pendingByContext = new();

    /// <summary>
    ///     Invoked after a successful save that enqueued outbox rows. Must not throw: the save has
    ///     already committed, and the poll interval is the correctness backstop for anything this
    ///     misses.
    /// </summary>
    /// <param name="context">The context whose save committed.</param>
    /// <param name="rowIds">Ids of the outbox rows this save inserted, for callers that need to await them.</param>
    protected abstract void OnOutboxRowsCommitted(DbContext context, IReadOnlyList<long> rowIds);

    /// <inheritdoc cref="OnOutboxRowsCommitted" />
    protected abstract Task OnOutboxRowsCommittedAsync(
        DbContext context,
        IReadOnlyList<long> rowIds,
        CancellationToken cancellationToken
    );

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result
    ) {
        MarkPending(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    ) {
        MarkPending(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result) {
        if (TryTakePending(eventData.Context, out var rowIds)) {
            OnOutboxRowsCommitted(eventData.Context!, rowIds);
        }

        return base.SavedChanges(eventData, result);
    }

    /// <inheritdoc />
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    ) {
        if (TryTakePending(eventData.Context, out var rowIds)) {
            await OnOutboxRowsCommittedAsync(eventData.Context!, rowIds, cancellationToken);
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override void SaveChangesFailed(DbContextErrorEventData eventData) {
        ClearPending(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    /// <inheritdoc />
    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default
    ) {
        ClearPending(eventData.Context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    /// <summary>Records the outbox rows this save is inserting, if any.</summary>
    private void MarkPending(DbContext? context) {
        if (context is null) {
            return;
        }

        var pending = _pendingByContext.GetValue(context, static _ => new List<SealedFgaOutboxEntry>());
        pending.Clear();
        // Only inserts matter: the drainer's own bookkeeping updates existing rows, and treating
        // those as new work would make it re-wake itself in a loop.
        pending.AddRange(
            context.ChangeTracker
                   .Entries<SealedFgaOutboxEntry>()
                   .Where(e => e.State == EntityState.Added)
                   .Select(e => e.Entity)
        );
    }

    /// <summary>Consumes the pending rows, reporting whether the committed save enqueued any.</summary>
    private bool TryTakePending(DbContext? context, out IReadOnlyList<long> rowIds) {
        if (context is null
            || !_pendingByContext.TryGetValue(context, out var pending)
            || pending.Count == 0) {
            rowIds = [];
            return false;
        }

        // Safe to read now and not a moment earlier: the save has completed, so EF has written the
        // database-generated keys back onto the tracked entities.
        rowIds = pending.Select(e => e.Id).ToList();
        pending.Clear();
        return true;
    }

    /// <summary>Drops the pending rows of a save that never committed.</summary>
    private void ClearPending(DbContext? context) {
        if (context is not null && _pendingByContext.TryGetValue(context, out var pending)) {
            pending.Clear();
        }
    }
}
