using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     Raises <see cref="SealedFgaOutboxSignal" /> after a <c>SaveChanges</c> that inserted outbox
///     rows, so a drainer in this process applies them immediately instead of on its next poll.
///     <para>
///         The signal is raised in <c>SavedChanges</c> rather than at enqueue time on purpose: the
///         drainer reads the outbox on its own connection, so rows are only visible to it once the
///         originating transaction has committed. Waking it earlier would consume the wake-up on a
///         pass that cannot yet see the rows.
///     </para>
///     <para>
///         Registered unconditionally by the generated <c>AddSealedFga</c>. It only reaches a drainer
///         in <i>this</i> process; <see cref="SealedFgaOutboxNotifyInterceptor" /> is the opt-in that
///         reaches other replicas.
///     </para>
/// </summary>
/// <param name="signal">The process-wide drainer wake-up hint.</param>
public class SealedFgaOutboxSignalInterceptor(SealedFgaOutboxSignal signal) : SealedFgaOutboxCommitInterceptor {
    /// <inheritdoc />
    protected override void OnOutboxRowsCommitted(DbContext context, IReadOnlyList<long> rowIds) => signal.Signal();

    /// <inheritdoc />
    protected override Task OnOutboxRowsCommittedAsync(
        DbContext context,
        IReadOnlyList<long> rowIds,
        CancellationToken cancellationToken
    ) {
        signal.Signal();
        return Task.CompletedTask;
    }
}
