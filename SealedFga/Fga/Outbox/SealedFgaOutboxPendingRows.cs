using System.Collections.Generic;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     The outbox rows enqueued so far within one DI scope — in a web app, one request. Filled by
///     <see cref="SealedFgaOutboxTrackingInterceptor" /> and consumed by
///     <see cref="SealedFgaOutboxWaiter{TDbContext}" />, which is what lets a request wait for
///     exactly its own tuple changes to reach OpenFGA instead of guessing at a delay.
///     <para>
///         Scoped, and never touched concurrently in practice: a request enqueues from its own
///         <c>SaveChanges</c> calls, which EF already forbids running concurrently on one context.
///         Access is still locked, because a scope may hold several contexts and background scopes
///         are free to save from parallel tasks.
///     </para>
/// </summary>
public sealed class SealedFgaOutboxPendingRows {
    private readonly object _gate = new();
    private readonly List<long> _rowIds = [];
    private bool _overflowed;

    /// <summary>
    ///     Records rows enqueued by a completed save. Beyond <paramref name="maxRows" /> the ids are
    ///     dropped and the scope is marked overflowed: a bulk fan-out is not something a request
    ///     should block on, and remembering every id would let a long-lived background scope grow
    ///     without bound.
    /// </summary>
    public void Add(IReadOnlyList<long> rowIds, int maxRows) {
        lock (_gate) {
            if (_overflowed) {
                return;
            }

            if (_rowIds.Count + rowIds.Count > maxRows) {
                _overflowed = true;
                _rowIds.Clear();
                return;
            }

            _rowIds.AddRange(rowIds);
        }
    }

    /// <summary>
    ///     Takes the rows recorded so far and resets the scope, so a second wait in the same scope
    ///     only covers what was enqueued since the first.
    /// </summary>
    /// <returns>
    ///     The recorded row ids, and whether the scope overflowed — in which case the ids are
    ///     incomplete and callers should not treat draining them as covering the scope.
    /// </returns>
    public (IReadOnlyList<long> RowIds, bool Overflowed) Take() {
        lock (_gate) {
            var taken = _rowIds.ToArray();
            _rowIds.Clear();
            var overflowed = _overflowed;
            _overflowed = false;
            return (taken, overflowed);
        }
    }
}
