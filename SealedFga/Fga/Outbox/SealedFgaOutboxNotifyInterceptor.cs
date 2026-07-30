using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     Hands a completed outbox-enqueueing save to an <see cref="ISealedFgaOutboxNotifier" />, so the
///     replica that holds the drainer lease learns about it even though it was not the one that
///     enqueued. Complements <see cref="SealedFgaOutboxSignalInterceptor" />, which only ever reaches
///     a drainer in the enqueuing process.
///     <para>
///         Unlike the signal interceptor this one is <b>not</b> registered automatically: it performs
///         I/O on the consumer's own connection during their <c>SaveChanges</c>, which is not
///         something to switch on behind their back. Consumers opt in per context — see
///         <c>AddSealedFgaNpgsqlNotify</c> in <c>SealedFga.Npgsql</c>.
///     </para>
/// </summary>
/// <param name="notifier">The transport that carries the wake-up to the other replicas.</param>
public class SealedFgaOutboxNotifyInterceptor(ISealedFgaOutboxNotifier notifier) : SealedFgaOutboxCommitInterceptor {
    /// <inheritdoc />
    protected override void OnOutboxRowsCommitted(DbContext context, IReadOnlyList<long> rowIds) =>
        // The synchronous SaveChanges path is already blocking its caller's thread, and the notifier
        // runs a single statement on a connection nobody else can be using at this point.
        NotifySafeAsync(context, CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc />
    protected override Task OnOutboxRowsCommittedAsync(
        DbContext context,
        IReadOnlyList<long> rowIds,
        CancellationToken cancellationToken
    ) => NotifySafeAsync(context, cancellationToken);

    /// <summary>
    ///     The rows are committed and the drainer's poll will find them regardless, so a notifier
    ///     that cannot deliver costs latency, not correctness — and must never turn a successful
    ///     <c>SaveChanges</c> into a failed one. Implementations are expected to log; swallowing here
    ///     is the last line of defence against one taking down a consumer's write path.
    /// </summary>
    private async Task NotifySafeAsync(DbContext context, CancellationToken cancellationToken) {
        try {
            await notifier.NotifyAsync(context, cancellationToken);
        } catch (Exception) {
            // Intentionally ignored; see summary.
        }
    }
}
