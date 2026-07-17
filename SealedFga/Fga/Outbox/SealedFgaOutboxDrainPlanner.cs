using System;
using System.Collections.Generic;
using System.Linq;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     The outcome of planning one claimed drain batch: which rows are superseded (marked
///     processed without an OpenFGA call), which are deferred (blocked by a fence or by a row a
///     fence must wait for), and the ordered segments of live rows to apply.
/// </summary>
internal sealed class DrainPlan {
    /// <summary>Rows whose intent is already covered by a newer processed row/fence.</summary>
    public List<(SealedFgaOutboxEntry Row, long SupersededById)> Superseded { get; } = [];

    /// <summary>Rows that must not apply this pass; retried once the blocker resolves.</summary>
    public List<(SealedFgaOutboxEntry Row, long BlockedById, bool BlockerParked, DateTime NextAttemptUtc)> Deferred { get; } = [];

    /// <summary>
    ///     Live work in application order. Each segment's tuple rows (already coalesced per key)
    ///     are applied as one batched write, then the segment's fence — if any — is applied
    ///     individually.
    /// </summary>
    public List<DrainSegment> Segments { get; } = [];
}

/// <summary>A run of coalesced live tuple rows, optionally terminated by a live fence.</summary>
internal sealed class DrainSegment {
    public List<SealedFgaOutboxEntry> ApplyRows { get; } = [];
    public SealedFgaOutboxEntry? Fence { get; set; }
}

/// <summary>
///     Pure planning core of the outbox drainer (no I/O): applies the ordering rules —
///     newest-wins supersession per tuple key, fence supersession, per-entity
///     <c>DeleteAllForObject</c> fences with asymmetric parked semantics, deferral instead of
///     failure for blocked rows — plus segmentation and per-key coalescing.
/// </summary>
internal static class SealedFgaOutboxDrainPlanner {
    /// <param name="claimed">The claimed batch, in ascending <c>Id</c> order (tracked rows).</param>
    /// <param name="blockers">
    ///     Unprocessed rows older than the claimed window's end that are currently ineligible
    ///     (backed-off or parked) and can therefore constrain claimed rows: all such fences, plus
    ///     non-parked tuple rows matching any claimed fence's entity.
    /// </param>
    /// <param name="witnesses">
    ///     Processed tuple rows with <c>Id</c> above the claimed window's start whose keys may
    ///     supersede claimed rows (a superset by column match is fine; keys are re-checked here).
    /// </param>
    /// <param name="processedFences">Processed fences with <c>Id</c> above the claimed window's start.</param>
    /// <param name="now">Current UTC time.</param>
    /// <param name="maxAttempts">Attempts threshold marking a row as permanently parked.</param>
    /// <param name="maxBackoff">Deferral window applied when the blocker is parked.</param>
    public static DrainPlan Plan(
        IReadOnlyList<SealedFgaOutboxEntry> claimed,
        IReadOnlyList<SealedFgaOutboxEntry> blockers,
        IReadOnlyList<SealedFgaOutboxEntry> witnesses,
        IReadOnlyList<SealedFgaOutboxEntry> processedFences,
        DateTime now,
        int maxAttempts,
        TimeSpan maxBackoff
    ) {
        var plan = new DrainPlan();

        // Newest processed Id per tuple key, for the newest-wins skip rule.
        var witnessMaxIdByKey = witnesses
                               .Where(w => !OutboxEntryMatching.IsFence(w))
                               .GroupBy(OutboxEntryMatching.KeyOf)
                               .ToDictionary(g => g.Key, g => g.Max(w => w.Id));

        // Two in-pass blocked sets with different reach (tuple rows only order against fences and
        // same-key rows, never against unrelated tuple rows):
        // - fenceBlockedEntities gates later FENCES: fed by every deferral (a deferred row is
        //   older and unprocessed, so no same-entity fence may overtake it).
        // - rowBlockedTargets gates later TUPLE ROWS: fed only by deferred fences (rows behind a
        //   deferred fence for that entity must wait for it).
        var fenceBlockedEntities = new Dictionary<string, (long BlockerId, bool Parked, DateTime NextAttemptUtc)>();
        var rowBlockedTargets = new Dictionary<string, (long BlockerId, bool Parked, DateTime NextAttemptUtc)>();

        var segment = new DrainSegment();
        var fenceBlockers = blockers.Where(OutboxEntryMatching.IsFence).ToList();
        var tupleBlockers = blockers.Where(b => !OutboxEntryMatching.IsFence(b)).ToList();

        void DeferResolved(SealedFgaOutboxEntry row, long blockerId, bool parked, DateTime nextAttempt) {
            plan.Deferred.Add((row, blockerId, parked, nextAttempt));
            foreach (var entity in EntitiesOfAny(row)) {
                if (!fenceBlockedEntities.ContainsKey(entity)) {
                    fenceBlockedEntities[entity] = (blockerId, parked, nextAttempt);
                }
            }

            if (OutboxEntryMatching.IsFence(row) && !rowBlockedTargets.ContainsKey(row.TargetId!)) {
                rowBlockedTargets[row.TargetId!] = (blockerId, parked, nextAttempt);
            }
        }

        void Defer(SealedFgaOutboxEntry row, SealedFgaOutboxEntry blocker) {
            var parked = IsParked(blocker, maxAttempts);
            var nextAttempt = parked
                ? now.Add(maxBackoff)
                : Max(now, blocker.NextAttemptUtc ?? now);
            DeferResolved(row, blocker.Id, parked, nextAttempt);
        }

        foreach (var row in claimed) {
            if (!OutboxEntryMatching.IsFence(row)) {
                // Rule 1 — newest-wins: a processed same-key row with higher Id supersedes this one.
                if (witnessMaxIdByKey.TryGetValue(OutboxEntryMatching.KeyOf(row), out var witnessId)
                    && witnessId > row.Id) {
                    plan.Superseded.Add((row, witnessId));
                    continue;
                }

                // Rule 2 — fence supersession: a processed same-entity fence with higher Id means
                // this row's intent was wiped by the entity purge (Id order is not commit order;
                // rows can become visible after a later fence already applied). Applying it would
                // resurrect tuples of a deleted entity.
                var supersedingFence = processedFences
                                      .Where(f => f.Id > row.Id && OutboxEntryMatching.FenceMatchesTupleRow(f.TargetId!, row))
                                      .OrderBy(f => f.Id)
                                      .FirstOrDefault();
                if (supersedingFence != null) {
                    plan.Superseded.Add((row, supersedingFence.Id));
                    continue;
                }

                // Deferral: an older ineligible fence for this row's entity (parked fences block —
                // asymmetric rule), or an entity already blocked earlier in this pass.
                var blockingFence = fenceBlockers
                                   .Where(f => f.Id < row.Id && OutboxEntryMatching.FenceMatchesTupleRow(f.TargetId!, row))
                                   .OrderBy(f => f.Id)
                                   .FirstOrDefault();
                if (blockingFence != null) {
                    Defer(row, blockingFence);
                    continue;
                }

                var blockedEntity = OutboxEntryMatching.EntitiesOf(row)
                                                       .FirstOrDefault(rowBlockedTargets.ContainsKey);
                if (blockedEntity != null) {
                    var b = rowBlockedTargets[blockedEntity];
                    DeferResolved(row, b.BlockerId, b.Parked, b.NextAttemptUtc);
                    continue;
                }

                segment.ApplyRows.Add(row);
            } else {
                // Fence. Blocked by: an older backed-off (NOT parked — asymmetric rule) tuple row
                // referencing its entity; an older ineligible fence for the same target (parked or
                // backed-off); or an entity blocked earlier in this pass.
                var blockingTuple = tupleBlockers
                                   .Where(t => t.Id < row.Id
                                               && !IsParked(t, maxAttempts)
                                               && OutboxEntryMatching.FenceMatchesTupleRow(row.TargetId!, t))
                                   .OrderBy(t => t.Id)
                                   .FirstOrDefault();
                var blockingFence = fenceBlockers
                                   .Where(f => f.Id < row.Id && f.TargetId == row.TargetId)
                                   .OrderBy(f => f.Id)
                                   .FirstOrDefault();

                if (blockingTuple != null || blockingFence != null) {
                    var blocker = blockingTuple != null
                                  && (blockingFence == null || blockingTuple.Id < blockingFence.Id)
                        ? blockingTuple
                        : blockingFence!;
                    Defer(row, blocker);
                    continue;
                }

                if (fenceBlockedEntities.TryGetValue(row.TargetId!, out var blocked)) {
                    DeferResolved(row, blocked.BlockerId, blocked.Parked, blocked.NextAttemptUtc);
                    continue;
                }

                // Live fence: closes the current segment (a global split — simpler than per-entity
                // and strictly safe; the only cost is that unrelated same-key rows on both sides
                // are applied in two idempotent writes instead of coalescing into one).
                CoalesceInto(plan, segment);
                segment.Fence = row;
                plan.Segments.Add(segment);
                segment = new DrainSegment();
            }
        }

        CoalesceInto(plan, segment);
        if (segment.ApplyRows.Count > 0) {
            plan.Segments.Add(segment);
        }

        return plan;
    }

    /// <summary>
    ///     Per-key coalescing within a segment: the newest live row per tuple key is applied; the
    ///     older ones are superseded by it (their intent is covered — and OpenFGA rejects
    ///     duplicate tuples within a single write request).
    /// </summary>
    private static void CoalesceInto(DrainPlan plan, DrainSegment segment) {
        if (segment.ApplyRows.Count < 2) {
            return;
        }

        var newestByKey = segment.ApplyRows
                                 .GroupBy(OutboxEntryMatching.KeyOf)
                                 .ToDictionary(g => g.Key, g => g.Max(r => r.Id));
        var coalesced = new List<SealedFgaOutboxEntry>();
        foreach (var row in segment.ApplyRows) {
            var newestId = newestByKey[OutboxEntryMatching.KeyOf(row)];
            if (row.Id == newestId) {
                coalesced.Add(row);
            } else {
                plan.Superseded.Add((row, newestId));
            }
        }

        segment.ApplyRows.Clear();
        segment.ApplyRows.AddRange(coalesced);
    }

    internal static bool IsParked(SealedFgaOutboxEntry entry, int maxAttempts)
        => entry.Attempts >= maxAttempts;

    /// <summary>The entity strings a row references — tuple entities, or the fence's target.</summary>
    private static IEnumerable<string> EntitiesOfAny(SealedFgaOutboxEntry row)
        => OutboxEntryMatching.IsFence(row) ? [row.TargetId!] : OutboxEntryMatching.EntitiesOf(row);

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
