using System;

namespace SealedFga;

/// <summary>
///     Configuration options for SealedFGA.
/// </summary>
public class SealedFgaOptions {
    /// <summary>
    ///     Base URL of the OpenFGA HTTP API (e.g. <c>http://localhost:8080</c>). Used to build the
    ///     <c>OpenFgaClient</c> that SealedFGA registers unless the consumer registered their own.
    /// </summary>
    public string? ApiUrl { get; set; }

    /// <summary>The OpenFGA store id SealedFGA reads/writes tuples against.</summary>
    public string? StoreId { get; set; }

    /// <summary>
    ///     The OpenFGA authorization model id to pin checks/writes to. When null, OpenFGA uses the
    ///     store's latest model.
    /// </summary>
    public string? AuthorizationModelId { get; set; }

    /// <summary>
    ///     The claim type on <c>HttpContext.User</c> that carries the OpenFGA subject (e.g.
    ///     <c>user:some-id</c>). Read by the FGA model binders to identify the acting user.
    /// </summary>
    public string UserClaimType { get; set; } = "open_fga_user";

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
    ///     How long the single-drainer lease is valid before other replicas may take over. The
    ///     holder renews ahead of every drain pass (seconds), so the duration only needs to cover
    ///     renewal cadence plus inter-replica clock skew; keep it small so a crashed leader is
    ///     replaced quickly (worst-case takeover ≈ lease duration + <see cref="OutboxPollInterval" />).
    /// </summary>
    public TimeSpan OutboxLeaseDuration { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     How long processed outbox rows are retained before the periodic sweep deletes them.
    ///     The outbox is a queue with recent-history diagnostics, not an audit log — growth is
    ///     bounded by default. Set to <c>null</c> to opt out and keep processed rows forever.
    /// </summary>
    public TimeSpan? OutboxRetentionPeriod { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    ///     Oldest-pending-age threshold above which <c>SealedFgaOutboxHealthCheck</c> reports
    ///     degraded (the backlog is not draining). Set to <c>null</c> to disable the age check;
    ///     parked rows always report unhealthy regardless.
    /// </summary>
    public TimeSpan? OutboxHealthDegradedPendingAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Maximum number of tuple write+delete operations sent to OpenFGA in a single
    ///     <c>Write</c> request (the SDK's non-transactional <c>MaxPerChunk</c>). OpenFGA rejects
    ///     a single request above its per-write limit (default 100), so larger operations are
    ///     split into independent chunked requests of this size. A large operation is therefore
    ///     not atomic across chunks; this is safe here as the write/delete paths use server-side
    ///     ignore semantics (duplicate writes / missing deletes) and are re-runnable.
    /// </summary>
    public int MaxTuplesPerWrite { get; set; } = 100;

    /// <summary>
    ///     How long a fetched authorization model is cached when the <c>OpenFgaClient</c> is
    ///     <b>not</b> pinned to an <c>AuthorizationModelId</c> (the server then resolves the
    ///     store's latest model, which can move, e.g. on a redeploy). With a pinned model ID the
    ///     model is cached indefinitely (models are immutable per ID) and this value is ignored.
    /// </summary>
    public TimeSpan AuthorizationModelCacheTtl { get; set; } = TimeSpan.FromMinutes(5);
}
