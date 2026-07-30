# SealedFga.Npgsql

Cross-replica wake-up for the SealedFGA outbox drainer, over PostgreSQL `LISTEN`/`NOTIFY`.

## The problem it solves

SealedFGA applies tuple changes asynchronously: `SaveChanges` writes outbox rows, and a background
drainer applies them to OpenFGA. Only the replica holding the drainer lease drains, and leadership
is sticky.

On a single replica the in-process `SealedFgaOutboxSignal` closes the gap — the drainer wakes the
moment a save commits. On **two or more replicas it usually cannot**: roughly every enqueue that
lands on a non-leader wakes a drainer that is not allowed to run, while the leader — whose signal
nobody raised — sleeps out `OutboxPollInterval`. For that window a caller that just created an
entity fails permission checks against it.

This package carries the wake-up across the process boundary, so the leader reacts to any replica's
commit.

## Wiring

```csharp
services.AddDbContext<MyDbContext>((sp, options) => {
    options.UseNpgsql(connectionString);
    options.AddSealedFga(sp);          // generated
    options.AddSealedFgaNpgsqlNotify(sp);
});

services.ConfigureSealedFga<MyDbContext>(configuration);
services.AddSealedFgaNpgsqlSignaling<MyDbContext>();

// Optional but recommended — a dead listener is otherwise silent (see below).
services.AddHealthChecks().AddSealedFgaNpgsqlOutboxListenerCheck<MyDbContext>();
```

Both calls are needed: `AddSealedFgaNpgsqlSignaling` registers the listener and the notifier,
`AddSealedFgaNpgsqlNotify` attaches the notifier to that context's saves. The listener defaults its
connection string to the context's own.

The in-process signal stays active alongside this — it saves a round trip when the leader is the one
enqueuing, and keeps working if the listener is down.

## Requirement: session-level connections

**A transaction-mode connection pooler silently breaks this.** PgBouncer's `pool_mode = transaction`
(and equivalents) multiplex one server connection across many clients, so the session holding a
`LISTEN` subscription stops being the session later statements land on. Nothing errors — the
notifications simply never arrive.

Direct connections and `pool_mode = session` are both fine. If only the pooler is reachable in
transaction mode, point `ListenerConnectionString` at a direct connection.

## Failure behavior

Delivery is best-effort by design, because it does not need to be more:

- A failed `NOTIFY` is logged and swallowed — the rows are committed and the drainer's poll still
  finds them. It never fails the consumer's `SaveChanges`.
- The listener reconnects with exponential backoff, and forces one drain pass after every
  (re)connect, since notifications are not queued for absent listeners.
- The health check reports **degraded**, never unhealthy: losing the listener costs latency, not
  correctness, and should not pull an instance out of rotation.

A silently dead `LISTEN` connection is the failure mode worth guarding: an idle subscription sends
nothing, so firewalls and load balancers reap it, and a dead socket stops delivering without raising
an error. The listener therefore sets Npgsql's `KeepAlive` (30s by default) so the failure surfaces
as a reconnect rather than as unexplained latency.

## Transactional behavior

The `NOTIFY` is issued on the **context's own connection**, so PostgreSQL's commit-time delivery
makes it inherit the caller's transaction outcome:

| Caller | Delivered |
|---|---|
| plain `SaveChanges` | immediately |
| inside an explicit transaction | at `Commit()` — exactly when the rows become visible |
| transaction rolled back | never |

That last pair is why a separate connection would be wrong: it would wake the leader while the rows
were still invisible, and the leader would find nothing and go back to sleep.
