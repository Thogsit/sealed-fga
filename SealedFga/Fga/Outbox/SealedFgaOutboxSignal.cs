using System.Threading;
using System.Threading.Tasks;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     In-process wake-up hint from the enqueue side to the outbox drainer, so a committed tuple
///     change is applied within milliseconds instead of waiting out
///     <see cref="SealedFgaOptions.OutboxPollInterval" />. This closes the read-after-write window
///     in which a caller that just committed an entity cannot yet see it through an OpenFGA check.
///     <para>
///         <b>It is a hint, not a delivery guarantee.</b> The signal is process-local, while the
///         drainer runs on whichever replica holds the lease — an enqueue on a non-leader replica
///         is still only picked up by the leader's next poll. The poll interval therefore remains
///         the correctness backstop; the signal only removes the latency in the common case.
///     </para>
/// </summary>
public sealed class SealedFgaOutboxSignal {
    private TaskCompletionSource _source = NewSource();

    /// <summary>
    ///     Raises the signal. Cheap, non-blocking and idempotent: signalling repeatedly before the
    ///     drainer wakes collapses into a single wake-up.
    /// </summary>
    public void Signal() => Volatile.Read(ref _source).TrySetResult();

    /// <summary>
    ///     Returns a task that completes on the next signal, consuming and re-arming an already
    ///     raised one. Call it <b>after</b> a drain pass and await it before the next: a signal
    ///     raised while that pass was running leaves the returned task already completed, so a
    ///     wake-up can never be lost between passes.
    /// </summary>
    public Task WaitAsync() {
        var current = Volatile.Read(ref _source);
        if (!current.Task.IsCompleted) {
            return current.Task;
        }

        // Re-arm for the next signal. A losing racer here simply observes the already-completed
        // task and drains too, which is harmless.
        Interlocked.CompareExchange(ref _source, NewSource(), current);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Continuations must not run inline on the thread that called <see cref="Signal" /> — that
    ///     thread is finishing a consumer's SaveChanges, not draining the outbox.
    /// </summary>
    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
