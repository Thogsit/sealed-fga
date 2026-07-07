using System;

namespace SealedFga;

/// <summary>
///     Configuration options for SealedFGA.
/// </summary>
public class SealedFgaOptions {
    /// <summary>
    ///     Whether the built-in background outbox drainer should run. When <c>true</c> (default), a
    ///     hosted service periodically applies queued relation changes to OpenFGA. Set to <c>false</c>
    ///     to disable it and drive <c>SealedFgaOutboxDrainer</c> yourself (e.g. in tests or a separate
    ///     worker process).
    /// </summary>
    public bool QueueFgaServiceOperations { get; set; } = true;

    /// <summary>How long the drainer waits between polls when the outbox is empty.</summary>
    public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum number of outbox rows processed per drain pass.</summary>
    public int OutboxBatchSize { get; set; } = 100;

    /// <summary>
    ///     Maximum number of apply attempts before an outbox row is left as permanently failed
    ///     (still retained for inspection, no longer retried).
    /// </summary>
    public int OutboxMaxAttempts { get; set; } = 10;

    /// <summary>
    ///     Maximum number of tuple write+delete operations sent to OpenFGA in a single
    ///     <c>Write</c> transaction. OpenFGA rejects transactions above its per-write limit
    ///     (default 100), so larger operations are split into sequential chunks of this size.
    ///     Because each chunk is a separate transaction, a large operation is not atomic across
    ///     chunks; this is safe here as the write/delete paths are idempotent and re-runnable.
    /// </summary>
    public int MaxTuplesPerWrite { get; set; } = 100;
}
