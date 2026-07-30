using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     Records the outbox rows a save enqueued into <see cref="SealedFgaOutboxPendingRows" />, so the
///     surrounding scope can later wait for exactly those rows to be applied
///     (<see cref="SealedFgaOutboxWaiter{TDbContext}" />).
///     <para>
///         Registered alongside <see cref="SealedFgaOutboxSignalInterceptor" /> and equally cheap —
///         it appends a handful of longs to a scoped list. Whether anything then waits is decided by
///         <see cref="SealedFgaOptions.WaitForOutboxOnRequestEnd" /> and by explicit calls to the
///         waiter, not here.
///     </para>
/// </summary>
/// <param name="pendingRows">The scope's record of enqueued rows.</param>
/// <param name="options">Supplies the per-scope cap on how many rows are worth remembering.</param>
public class SealedFgaOutboxTrackingInterceptor(
    SealedFgaOutboxPendingRows pendingRows,
    IOptions<SealedFgaOptions> options
) : SealedFgaOutboxCommitInterceptor {
    /// <inheritdoc />
    protected override void OnOutboxRowsCommitted(DbContext context, IReadOnlyList<long> rowIds) =>
        pendingRows.Add(rowIds, options.Value.OutboxWaitMaxRows);

    /// <inheritdoc />
    protected override Task OnOutboxRowsCommittedAsync(
        DbContext context,
        IReadOnlyList<long> rowIds,
        CancellationToken cancellationToken
    ) {
        pendingRows.Add(rowIds, options.Value.OutboxWaitMaxRows);
        return Task.CompletedTask;
    }
}
