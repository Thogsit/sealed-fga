using System;
using OpenFga.Sdk.Model;

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
    ///     <para>
    ///         This toggles <b>only</b> the drainer. It does not change how
    ///         <see cref="Fga.ISealedFgaService" />'s <c>WriteAsync</c>/<c>DeleteAsync</c> behave —
    ///         those always apply to OpenFGA immediately. Transaction-coupled tuple changes go through
    ///         the outbox via the annotation interceptor and the <c>DbContext.EnqueueFga*</c>
    ///         extensions, independently of this flag.
    ///     </para>
    /// </summary>
    public bool RunOutboxDrainer { get; set; } = true;

    /// <summary>
    ///     Deprecated alias for <see cref="RunOutboxDrainer" />. The old name wrongly implied it made
    ///     service writes queue; it has only ever toggled the background drainer. Kept for source
    ///     compatibility.
    /// </summary>
    [Obsolete(
        "Renamed to " + nameof(RunOutboxDrainer)
        + "; this flag only toggles the outbox drainer, it does not make WriteAsync/DeleteAsync queue."
    )]
    public bool QueueFgaServiceOperations {
        get => RunOutboxDrainer;
        set => RunOutboxDrainer = value;
    }

    /// <summary>
    ///     Upper bound on how long the drainer waits between passes when the outbox is empty. A
    ///     commit that enqueued rows normally wakes it straight away via
    ///     <see cref="Fga.Outbox.SealedFgaOutboxSignal" />, so this only governs work the signal
    ///     cannot reach — most importantly rows enqueued by a replica that does not hold the
    ///     drainer lease.
    ///     <para>
    ///         Kept low because that gap is user-visible: a caller that just created an entity fails
    ///         permission checks until the drainer has applied its tuples. Deployments that wire up
    ///         cross-replica notification (see <c>SealedFga.Npgsql</c>) can safely raise it again,
    ///         since the signal then reaches the leader from any replica.
    ///     </para>
    /// </summary>
    public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     Whether a request is held at the end of its action until the outbox rows it enqueued have
    ///     been applied to OpenFGA, so a client that immediately reads back what it just created
    ///     cannot lose the race against the drainer.
    ///     <para>
    ///         On by default. Without it, a create endpoint returns while the tuple authorizing the
    ///         new entity is still queued, and the follow-up read fails its permission check — as a
    ///         missing list row at best, and an outright 403 on a chained call at worst. Because most
    ///         tuple changes come from <c>[SealedFgaRelation]</c> annotations and are invisible at the
    ///         call site, that hazard is easy to reintroduce and hard to spot, which is why the safe
    ///         behaviour is the default rather than something to remember to switch on.
    ///     </para>
    ///     <para>
    ///         The cost is the drain latency, added to mutating requests only. Deployments without a
    ///         cross-replica notifier pay up to <see cref="OutboxPollInterval" /> on whichever replica
    ///         does not hold the drainer lease; with one (see <c>SealedFga.Npgsql</c>) it is
    ///         milliseconds. Individual endpoints opt out with <c>[SealedFgaSkipOutboxWait]</c>.
    ///     </para>
    /// </summary>
    public bool WaitForOutboxOnRequestEnd { get; set; } = true;

    /// <summary>
    ///     How long that wait may last before giving up. On expiry the request completes normally and
    ///     a warning is logged: the write has already committed, so failing the caller would turn a
    ///     latency problem into an error, and the drainer still applies the rows afterwards.
    /// </summary>
    public TimeSpan OutboxWaitTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     How often the wait re-reads row state. The drainer usually finishes before the first check,
    ///     so this mostly governs how promptly a slower drain is noticed.
    /// </summary>
    public TimeSpan OutboxWaitPollInterval { get; set; } = TimeSpan.FromMilliseconds(25);

    /// <summary>
    ///     Most rows one scope may enqueue and still be waited on. A bulk fan-out is not something a
    ///     request should block on, so beyond this the wait is skipped (the drainer applies the rows
    ///     as usual). It also bounds what a long-lived background scope remembers.
    /// </summary>
    public int OutboxWaitMaxRows { get; set; } = 200;

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

    /// <summary>
    ///     Default read consistency applied to <b>list-shaped</b> operations
    ///     (<c>ListObjectsAsync</c>, <c>ListUsersAsync</c>, <c>ListRelationsAsync</c>, and the
    ///     <c>[FgaAuthorizeList]</c> binder) when a call supplies no explicit consistency. This lets a
    ///     consumer mirror "always <see cref="ConsistencyPreference.HIGHERCONSISTENCY" /> on list ops"
    ///     without threading options through every call site. A per-call
    ///     <c>SealedFgaQueryOptions.Consistency</c> always wins; <c>null</c> (default) leaves the
    ///     choice to the server. Single <c>CheckAsync</c>/<c>BatchCheckAsync</c> calls are unaffected —
    ///     this applies only to list operations.
    /// </summary>
    public ConsistencyPreference? DefaultListConsistency { get; set; }
}
