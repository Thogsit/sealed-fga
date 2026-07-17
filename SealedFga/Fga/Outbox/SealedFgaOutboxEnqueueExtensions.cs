using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using SealedFga.AuthModel;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     The typed public enqueue API for precomputed tuple diffs. Producers whose tuple changes no annotation can
///     express (computed grants, cascades, fan-outs) enqueue them here instead of hand-rolling a
///     side queue.
///     <para>
///         All methods are synchronous and local: they add <see cref="SealedFgaOutboxEntry" /> rows
///         to the <b>ambient change tracker</b> — nothing talks to OpenFGA at enqueue time, and the
///         rows are persisted by the caller's own <c>SaveChanges</c>/transaction commit, exactly like
///         the rows produced by the annotation interceptor. Rolls back with the transaction; applied
///         later by the background drainer with the outbox contract's semantics (at-least-once,
///         server-side idempotent, newest-wins per tuple key, single leased applier, batched).
///     </para>
/// </summary>
public static class SealedFgaOutboxEnqueueExtensions {
    /// <summary>
    ///     Enqueues one tuple <b>write</b>. Userset subjects (<c>type:id#relation</c>) work via
    ///     <c>SealedFgaUserset&lt;TUserId&gt;</c> as the <paramref name="user" />.
    /// </summary>
    /// <param name="db">A DbContext with the SealedFGA outbox configured.</param>
    /// <param name="user">The tuple's user/subject.</param>
    /// <param name="relation">The relation — bound to the object type at compile time.</param>
    /// <param name="objectId">The typed object ID.</param>
    public static void EnqueueFgaWrite<TObjId>(
        this DbContext db,
        ISealedFgaUser user,
        ISealedFgaRelation<TObjId> relation,
        TObjId objectId
    ) where TObjId : ISealedFgaTypeId<TObjId> {
        var op = SealedFgaTupleOperation.Of(user, relation, objectId);
        OutboxSetOf(db).Add(SealedFgaOutboxEntry.ForWrite(op.User, op.Relation, op.Object));
    }

    /// <summary>
    ///     Enqueues one tuple <b>delete</b>. Deleting a never-stored tuple is a server-side no-op
    ///     (ignore semantics), not an error.
    /// </summary>
    /// <param name="db">A DbContext with the SealedFGA outbox configured.</param>
    /// <param name="user">The tuple's user/subject.</param>
    /// <param name="relation">The relation — bound to the object type at compile time.</param>
    /// <param name="objectId">The typed object ID.</param>
    public static void EnqueueFgaDelete<TObjId>(
        this DbContext db,
        ISealedFgaUser user,
        ISealedFgaRelation<TObjId> relation,
        TObjId objectId
    ) where TObjId : ISealedFgaTypeId<TObjId> {
        var op = SealedFgaTupleOperation.Of(user, relation, objectId);
        OutboxSetOf(db).Add(SealedFgaOutboxEntry.ForDelete(op.User, op.Relation, op.Object));
    }

    /// <summary>
    ///     Enqueues a batch of writes and deletes in one call — the shape for 1,000+-row fan-outs.
    ///     Build the operations via <see cref="SealedFgaTupleOperation.Of{TObjId}" />.
    /// </summary>
    /// <param name="db">A DbContext with the SealedFGA outbox configured.</param>
    /// <param name="writes">Tuples that must exist.</param>
    /// <param name="deletes">Tuples that must not exist.</param>
    public static void EnqueueFga(
        this DbContext db,
        IEnumerable<SealedFgaTupleOperation> writes,
        IEnumerable<SealedFgaTupleOperation> deletes
    ) {
        ArgumentNullException.ThrowIfNull(writes);
        ArgumentNullException.ThrowIfNull(deletes);

        var rows = new List<SealedFgaOutboxEntry>();
        foreach (var op in writes) {
            EnsureNotDefault(op);
            rows.Add(SealedFgaOutboxEntry.ForWrite(op.User, op.Relation, op.Object));
        }

        foreach (var op in deletes) {
            EnsureNotDefault(op);
            rows.Add(SealedFgaOutboxEntry.ForDelete(op.User, op.Relation, op.Object));
        }

        if (rows.Count > 0) {
            OutboxSetOf(db).AddRange(rows);
        }
    }

    /// <summary>Rejects <c>default</c>-constructed operations before their nulls reach the outbox.</summary>
    private static void EnsureNotDefault(SealedFgaTupleOperation op) {
        if (op.IsDefault) {
            throw new ArgumentException(
                $"A default-constructed {nameof(SealedFgaTupleOperation)} carries no tuple; build "
                + $"operations via {nameof(SealedFgaTupleOperation)}.{nameof(SealedFgaTupleOperation.Of)}(...)."
            );
        }
    }

    /// <summary>
    ///     Resolves the outbox set, failing loud (instead of EF's generic error) when the context
    ///     does not have the SealedFGA outbox configured.
    /// </summary>
    private static DbSet<SealedFgaOutboxEntry> OutboxSetOf(DbContext db) {
        ArgumentNullException.ThrowIfNull(db);

        if (db.Model.FindEntityType(typeof(SealedFgaOutboxEntry)) is null) {
            throw new InvalidOperationException(
                $"DbContext '{db.GetType().Name}' does not have the SealedFGA outbox configured. "
                + "Wire it via options.AddSealedFga(sp) (or modelBuilder.ConfigureSealedFgaOutbox() "
                + "in OnModelCreating) before enqueuing tuple operations."
            );
        }

        return db.Set<SealedFgaOutboxEntry>();
    }
}
