using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     Cross-process counterpart to <see cref="SealedFgaOutboxSignal" />: told that a save enqueued
///     outbox rows, an implementation is responsible for reaching the replica that actually holds the
///     drainer lease. The in-process signal cannot do that — it wakes only the local drainer, and on a
///     multi-replica deployment roughly every enqueue that lands on a non-leader is invisible to the
///     leader until its next poll.
///     <para>
///         There is no default implementation and none is registered: without a notifier SealedFGA
///         behaves exactly as before, with <see cref="SealedFgaOptions.OutboxPollInterval" /> as the
///         cross-replica latency bound. <c>SealedFga.Npgsql</c> ships a <c>pg_notify</c>-based one.
///     </para>
///     <para>
///         <b>Implementations must be best-effort.</b> The row is already committed and the poll is
///         the correctness backstop, so a failed notification is a latency regression, never a
///         correctness one — never surface it as a failed <c>SaveChanges</c> to the caller.
///     </para>
/// </summary>
public interface ISealedFgaOutboxNotifier {
    /// <summary>
    ///     Announces that the just-completed save on <paramref name="context" /> enqueued outbox rows.
    ///     <para>
    ///         Called from the <c>SavedChanges</c> phase, so the context is still usable and — when
    ///         the consumer opened an explicit transaction — that transaction is still open. An
    ///         implementation that rides the context's own connection therefore inherits the
    ///         transaction's fate, which is what makes it safe to announce work that a rollback may
    ///         yet discard.
    ///     </para>
    /// </summary>
    /// <param name="context">The context whose save enqueued the rows.</param>
    /// <param name="cancellationToken">Cancellation for the notification itself.</param>
    Task NotifyAsync(DbContext context, CancellationToken cancellationToken = default);
}
