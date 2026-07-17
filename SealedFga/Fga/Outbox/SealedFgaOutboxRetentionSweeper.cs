using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     Periodic outbox cleanup, run by the drainer leader only: deletes processed rows older
///     than the retention window, but never a row that is still a newest-wins witness — a
///     processed row is kept while an older unprocessed same-key row (or, for fences, an older
///     unprocessed row referencing the fence's entity) exists, so reviving parked rows (e.g. by
///     raising <c>OutboxMaxAttempts</c>) can never resurrect intents whose supersession evidence
///     was swept. Also marks parked tuple rows as superseded once a newer processed same-key row
///     exists, which both de-noises the parked-row diagnostic and releases their witnesses for
///     the next sweep. Parked fences are never auto-cleared: they are meaningful blockers.
/// </summary>
internal static class SealedFgaOutboxRetentionSweeper {
    /// <returns>The number of rows deleted or cleaned up.</returns>
    internal static async Task<int> SweepAsync(
        DbContext context,
        TimeSpan? retentionPeriod,
        int maxAttempts,
        TimeProvider timeProvider,
        CancellationToken ct = default
    ) {
        if (retentionPeriod == null) {
            return 0;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = now.Subtract(retentionPeriod.Value);

        return context.Database.IsRelational()
            ? await SweepRelationalAsync(context, cutoff, now, maxAttempts, ct)
            : await SweepInMemoryAsync(context, cutoff, now, maxAttempts, ct);
    }

    private static async Task<int> SweepRelationalAsync(
        DbContext context,
        DateTime cutoff,
        DateTime now,
        int maxAttempts,
        CancellationToken ct
    ) {
        var set = context.Set<SealedFgaOutboxEntry>();

        // 1. Parked-tuple cleanup first, so their (now-processed) state releases witness
        //    protection within the same sweep cycle.
        var cleaned = await set
                           .Where(e => e.ProcessedAtUtc == null
                                       && e.Attempts >= maxAttempts
                                       && e.OperationType != SealedFgaOutboxOperationType.DeleteAllForObject
                                       && set.Any(w => w.ProcessedAtUtc != null
                                                       && w.Id > e.Id
                                                       && w.OperationType != SealedFgaOutboxOperationType.DeleteAllForObject
                                                       && w.TupleUser == e.TupleUser
                                                       && w.TupleRelation == e.TupleRelation
                                                       && w.TupleObject == e.TupleObject
                                        )
                            )
                           .ExecuteUpdateAsync(s => s
                                   .SetProperty(e => e.ProcessedAtUtc, now)
                                   .SetProperty(e => e.LastError, "Superseded by a newer processed row (parked-row cleanup)."),
                               ct
                            );

        // 2. Expired processed tuple rows, unless still a witness for an older unprocessed row.
        var deletedTuples = await set
                                 .Where(e => e.ProcessedAtUtc != null
                                             && e.ProcessedAtUtc < cutoff
                                             && e.OperationType != SealedFgaOutboxOperationType.DeleteAllForObject
                                             && !set.Any(p => p.ProcessedAtUtc == null
                                                              && p.Id < e.Id
                                                              && p.OperationType != SealedFgaOutboxOperationType.DeleteAllForObject
                                                              && p.TupleUser == e.TupleUser
                                                              && p.TupleRelation == e.TupleRelation
                                                              && p.TupleObject == e.TupleObject
                                              )
                                  )
                                 .ExecuteDeleteAsync(ct);

        // 3. Expired processed fences, unless an older unprocessed row still references their
        //    entity (the fence is that row's fence-supersession witness).
        var deletedFences = await set
                                 .Where(e => e.ProcessedAtUtc != null
                                             && e.ProcessedAtUtc < cutoff
                                             && e.OperationType == SealedFgaOutboxOperationType.DeleteAllForObject
                                             && !set.Any(p => p.ProcessedAtUtc == null
                                                              && p.Id < e.Id
                                                              && (p.OperationType == SealedFgaOutboxOperationType.DeleteAllForObject
                                                                  ? p.TargetId == e.TargetId
                                                                  : p.TupleObject == e.TargetId
                                                                    || p.TupleUser == e.TargetId
                                                                    || p.TupleUser!.StartsWith(e.TargetId + "#"))
                                              )
                                  )
                                 .ExecuteDeleteAsync(ct);

        return cleaned + deletedTuples + deletedFences;
    }

    /// <summary>
    ///     Non-relational fallback (InMemory: tests/dev, small tables by definition) — the same
    ///     rules evaluated in memory, since ExecuteUpdate/ExecuteDelete need a relational provider.
    /// </summary>
    private static async Task<int> SweepInMemoryAsync(
        DbContext context,
        DateTime cutoff,
        DateTime now,
        int maxAttempts,
        CancellationToken ct
    ) {
        var set = context.Set<SealedFgaOutboxEntry>();
        var all = await set.ToListAsync(ct);
        var changed = 0;

        foreach (var parked in all.Where(e => e.ProcessedAtUtc == null
                                              && e.Attempts >= maxAttempts
                                              && !OutboxEntryMatching.IsFence(e)
                                              && all.Any(w => w.ProcessedAtUtc != null
                                                              && w.Id > e.Id
                                                              && !OutboxEntryMatching.IsFence(w)
                                                              && OutboxEntryMatching.KeyOf(w) == OutboxEntryMatching.KeyOf(e)
                                               )
                 )) {
            parked.ProcessedAtUtc = now;
            parked.LastError = "Superseded by a newer processed row (parked-row cleanup).";
            changed++;
        }

        var expired = all.Where(e => e.ProcessedAtUtc != null && e.ProcessedAtUtc < cutoff);
        foreach (var row in expired) {
            var isWitness = OutboxEntryMatching.IsFence(row)
                ? all.Any(p => p.ProcessedAtUtc == null
                               && p.Id < row.Id
                               && (OutboxEntryMatching.IsFence(p)
                                   ? p.TargetId == row.TargetId
                                   : OutboxEntryMatching.FenceMatchesTupleRow(row.TargetId!, p)))
                : all.Any(p => p.ProcessedAtUtc == null
                               && p.Id < row.Id
                               && !OutboxEntryMatching.IsFence(p)
                               && OutboxEntryMatching.KeyOf(p) == OutboxEntryMatching.KeyOf(row));
            if (!isWitness) {
                set.Remove(row);
                changed++;
            }
        }

        await context.SaveChangesAsync(ct);
        return changed;
    }
}
