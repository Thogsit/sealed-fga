using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using OpenFga.Sdk.Exceptions;
using OpenFga.Sdk.Model;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     Applies pending <see cref="SealedFgaOutboxEntry" /> rows to OpenFGA. Contains no scheduling
///     or leader election of its own; the hosted service (<c>SealedFgaOutboxHostedService</c>)
///     drives it on a loop under the drainer lease. Correctness of the newest-wins ordering rules
///     assumes a single active drainer per database.
/// </summary>
public static class SealedFgaOutboxDrainer {
    /// <summary>The largest backoff between retries, in seconds. Also used as the deferral window
    /// for rows blocked by a permanently parked fence.</summary>
    private const int MaxBackoffSeconds = 300;

    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(MaxBackoffSeconds);

    /// <summary>
    ///     Processes up to <paramref name="batchSize" /> eligible pending rows in ascending
    ///     <c>Id</c> order, subject to the outbox ordering rules: rows superseded by a newer
    ///     processed same-key row (or by a processed same-entity <c>DeleteAllForObject</c> fence)
    ///     are marked processed without an OpenFGA call; rows blocked by a fence are deferred
    ///     (retry time bumped, attempts untouched); the surviving rows are coalesced per tuple key
    ///     and applied in batched non-transactional writes, with per-tuple failures mapped back to
    ///     their rows for backoff bookkeeping. Runs in the caller's DI scope.
    /// </summary>
    /// <returns>
    ///     The number of rows whose state changed in this pass (applied, superseded, deferred, or
    ///     failure-bumped) — every state change makes a row ineligible for the next immediate
    ///     pass, so a caller can safely loop until this returns 0.
    /// </returns>
    public static async Task<int> DrainOnceAsync(
        DbContext context,
        SealedFgaService fgaService,
        int batchSize,
        int maxAttempts,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default
    ) {
        var tp = timeProvider ?? TimeProvider.System;
        var now = tp.GetUtcNow().UtcDateTime;
        var set = context.Set<SealedFgaOutboxEntry>();

        var claimed = await set
                           .Where(e => e.ProcessedAtUtc == null
                                       && e.Attempts < maxAttempts
                                       && (e.NextAttemptUtc == null || e.NextAttemptUtc <= now)
                            )
                           .OrderBy(e => e.Id)
                           .Take(batchSize)
                           .ToListAsync(ct);

        if (claimed.Count == 0) {
            return 0;
        }

        var (blockers, witnesses, processedFences) =
            await LoadPlanningContextAsync(set, claimed, maxAttempts, now, ct);

        var plan = SealedFgaOutboxDrainPlanner.Plan(
            claimed,
            blockers,
            witnesses,
            processedFences,
            now,
            maxAttempts,
            MaxBackoff
        );

        var changed = 0;
        foreach (var (row, byId) in plan.Superseded) {
            MarkSuperseded(row, byId, now);
            changed++;
        }

        foreach (var (row, blockerId, parked, nextAttempt) in plan.Deferred) {
            MarkDeferred(row, blockerId, parked, nextAttempt);
            changed++;
        }

        changed += await ExecuteSegmentsAsync(plan.Segments, fgaService, now, ct);

        await context.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>
    ///     Loads the rows outside the claimed batch that the planner needs: older ineligible rows
    ///     that can block claimed rows, and newer processed rows/fences that can supersede them.
    ///     Every eligible row older than the claimed window's end is in the batch by construction,
    ///     so older out-of-batch blockers are exactly the ineligible (backed-off/parked) ones.
    /// </summary>
    private static async Task<(
        List<SealedFgaOutboxEntry> Blockers,
        List<SealedFgaOutboxEntry> Witnesses,
        List<SealedFgaOutboxEntry> ProcessedFences)> LoadPlanningContextAsync(
        DbSet<SealedFgaOutboxEntry> set,
        List<SealedFgaOutboxEntry> claimed,
        int maxAttempts,
        DateTime now,
        CancellationToken ct
    ) {
        var minId = claimed[0].Id;
        var maxId = claimed[claimed.Count - 1].Id;

        // Older ineligible fences can block any claimed row; only load ineligible tuple rows when
        // the claim contains fences they could block (parked tuple rows accumulate over time and
        // are irrelevant to a fence-free batch).
        var blockersQuery = set.AsNoTracking()
                               .Where(e => e.ProcessedAtUtc == null
                                           && e.Id < maxId
                                           && (e.Attempts >= maxAttempts
                                               || (e.NextAttemptUtc != null && e.NextAttemptUtc > now))
                                );
        var blockers = await blockersQuery
                            .Where(e => e.OperationType == SealedFgaOutboxOperationType.DeleteAllForObject)
                            .ToListAsync(ct);

        foreach (var fence in claimed.Where(OutboxEntryMatching.IsFence)) {
            var target = fence.TargetId!;
            var prefix = target + "#";
            // Parked tuple rows never block a fence (asymmetric rule), so only backed-off ones.
            var tupleBlockers = await blockersQuery
                                     .Where(e => e.OperationType != SealedFgaOutboxOperationType.DeleteAllForObject
                                                 && e.Attempts < maxAttempts
                                                 && (e.TupleObject == target
                                                     || e.TupleUser == target
                                                     || e.TupleUser!.StartsWith(prefix))
                                      )
                                     .ToListAsync(ct);
            blockers.AddRange(tupleBlockers.Where(t => blockers.All(b => b.Id != t.Id)));
        }

        // Newest-wins witnesses: processed tuple rows sharing the claimed rows' key columns.
        // Column-level IN filters give a superset; the planner re-checks exact keys.
        var witnesses = new List<SealedFgaOutboxEntry>();
        var claimedTuples = claimed.Where(e => !OutboxEntryMatching.IsFence(e)).ToList();
        if (claimedTuples.Count > 0) {
            var users = claimedTuples.Select(e => e.TupleUser).Distinct().ToList();
            var relations = claimedTuples.Select(e => e.TupleRelation).Distinct().ToList();
            var objects = claimedTuples.Select(e => e.TupleObject).Distinct().ToList();
            witnesses = await set.AsNoTracking()
                                 .Where(e => e.ProcessedAtUtc != null
                                             && e.Id > minId
                                             && e.OperationType != SealedFgaOutboxOperationType.DeleteAllForObject
                                             && users.Contains(e.TupleUser)
                                             && relations.Contains(e.TupleRelation)
                                             && objects.Contains(e.TupleObject)
                                  )
                                 .ToListAsync(ct);
        }

        var processedFences = await set.AsNoTracking()
                                       .Where(e => e.ProcessedAtUtc != null
                                                   && e.Id > minId
                                                   && e.OperationType == SealedFgaOutboxOperationType.DeleteAllForObject
                                        )
                                       .ToListAsync(ct);

        return (blockers, witnesses, processedFences);
    }

    /// <summary>
    ///     Applies the live segments in order: each segment's coalesced tuple rows as one batched
    ///     write (per-tuple failures mapped back to rows), then its fence individually. A failed
    ///     row blocks every later same-entity row in the pass (they defer to its retry time).
    /// </summary>
    private static async Task<int> ExecuteSegmentsAsync(
        List<DrainSegment> segments,
        SealedFgaService fgaService,
        DateTime now,
        CancellationToken ct
    ) {
        var changed = 0;
        // Same split as the planner's blocked sets: entities of failed rows/fences gate later
        // FENCES (nothing same-entity may overtake an unapplied older row); only failed fences'
        // targets gate later TUPLE ROWS (unrelated tuple rows never block each other, and a
        // newer same-key row applying after an older one failed is exactly newest-wins).
        var fenceBlockedEntities = new Dictionary<string, (long RowId, DateTime NextAttemptUtc)>();
        var rowBlockedTargets = new Dictionary<string, (long RowId, DateTime NextAttemptUtc)>();

        void MarkFailed(SealedFgaOutboxEntry row, string error) {
            row.Attempts++;
            row.LastError = error;
            row.NextAttemptUtc = now.Add(ComputeBackoff(row.Attempts));
            foreach (var entity in OutboxEntryMatching.IsFence(row)
                         ? [row.TargetId!]
                         : OutboxEntryMatching.EntitiesOf(row)) {
                if (!fenceBlockedEntities.ContainsKey(entity)) {
                    fenceBlockedEntities[entity] = (row.Id, row.NextAttemptUtc.Value);
                }
            }

            if (OutboxEntryMatching.IsFence(row) && !rowBlockedTargets.ContainsKey(row.TargetId!)) {
                rowBlockedTargets[row.TargetId!] = (row.Id, row.NextAttemptUtc.Value);
            }
        }

        bool TryExecutionDefer(
            SealedFgaOutboxEntry row,
            IEnumerable<string> entities,
            Dictionary<string, (long RowId, DateTime NextAttemptUtc)> blockedSet
        ) {
            foreach (var entity in entities) {
                if (blockedSet.TryGetValue(entity, out var blocker)) {
                    MarkDeferred(row, blocker.RowId, parked: false, blocker.NextAttemptUtc);
                    return true;
                }
            }

            return false;
        }

        foreach (var segment in segments) {
            var applyRows = new List<SealedFgaOutboxEntry>();
            foreach (var row in segment.ApplyRows) {
                if (TryExecutionDefer(row, OutboxEntryMatching.EntitiesOf(row), rowBlockedTargets)) {
                    changed++;
                } else {
                    applyRows.Add(row);
                }
            }

            if (applyRows.Count > 0) {
                var writes = applyRows
                            .Where(r => r.OperationType == SealedFgaOutboxOperationType.WriteTuple)
                            .Select(ToTupleKey)
                            .ToList();
                var deletes = applyRows
                             .Where(r => r.OperationType == SealedFgaOutboxOperationType.DeleteTuple)
                             .Select(ToTupleKey)
                             .ToList();

                try {
                    var failures = await fgaService.WriteAndDeleteTuplesWithOutcomesAsync(writes, deletes, ct);
                    var failedKeys = failures
                                    .Select(f => ((string?) f.TupleKey.User, (string?) f.TupleKey.Relation,
                                                  (string?) f.TupleKey.Object))
                                    .ToHashSet();
                    // The SDK attributes failures per CHUNK, not per tuple: one server-rejected
                    // tuple blames its whole chunk. When a validation error is involved (a poison
                    // tuple, not a down server), isolate it by re-applying the blamed tuples one
                    // by one — writes are idempotent server-side — so innocent chunk-mates don't park with it.
                    var isolatePoison = applyRows.Count > 1
                                        && failures.Any(f => f.Error is FgaApiValidationError);
                    foreach (var row in applyRows) {
                        if (failedKeys.Contains(OutboxEntryMatching.KeyOf(row))) {
                            if (isolatePoison) {
                                await ApplySingleRowAsync(row, fgaService, now, MarkFailed, ct);
                            } else {
                                var failure = failures.First(f => f.TupleKey.User == row.TupleUser
                                                                  && f.TupleKey.Relation == row.TupleRelation
                                                                  && f.TupleKey.Object == row.TupleObject);
                                MarkFailed(row, failure.Error?.Message ?? "OpenFGA write failed.");
                            }
                        } else {
                            MarkProcessed(row, now);
                        }

                        changed++;
                    }
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    // Request-level failure (transport/auth/...): every row in the group failed.
                    foreach (var row in applyRows) {
                        MarkFailed(row, ex.Message);
                        changed++;
                    }
                }
            }

            if (segment.Fence != null) {
                var fence = segment.Fence;
                if (TryExecutionDefer(fence, [fence.TargetId!], fenceBlockedEntities)) {
                    // A deferred fence blocks later same-entity tuple rows in this pass, too.
                    if (!rowBlockedTargets.ContainsKey(fence.TargetId!)) {
                        rowBlockedTargets[fence.TargetId!] = (fence.Id, fence.NextAttemptUtc!.Value);
                    }

                    changed++;
                    continue;
                }

                try {
                    await fgaService.DeleteAllRelationsForRawObjectAsync(fence.TargetId!, fence.TypeName!, ct);
                    MarkProcessed(fence, now);
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    MarkFailed(fence, ex.Message);
                }

                changed++;
            }
        }

        return changed;
    }

    /// <summary>
    ///     Applies one tuple row in its own write request for exact failure attribution (the
    ///     poison-isolation path).
    /// </summary>
    private static async Task ApplySingleRowAsync(
        SealedFgaOutboxEntry row,
        SealedFgaService fgaService,
        DateTime now,
        Action<SealedFgaOutboxEntry, string> markFailed,
        CancellationToken ct
    ) {
        try {
            var isWrite = row.OperationType == SealedFgaOutboxOperationType.WriteTuple;
            var single = await fgaService.WriteAndDeleteTuplesWithOutcomesAsync(
                isWrite ? [ToTupleKey(row)] : [],
                isWrite ? [] : [ToTupleKey(row)],
                ct
            );
            if (single.Count == 0) {
                MarkProcessed(row, now);
            } else {
                markFailed(row, single[0].Error?.Message ?? "OpenFGA write failed.");
            }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            markFailed(row, ex.Message);
        }
    }

    private static void MarkProcessed(SealedFgaOutboxEntry row, DateTime now) {
        row.ProcessedAtUtc = now;
        row.LastError = null;
    }

    private static void MarkSuperseded(SealedFgaOutboxEntry row, long byId, DateTime now) {
        row.ProcessedAtUtc = now;
        row.LastError = $"Superseded by outbox row #{byId}.";
    }

    private static void MarkDeferred(SealedFgaOutboxEntry row, long blockerId, bool parked, DateTime nextAttempt) {
        // Deferral is not failure: Attempts stays untouched so a blocked row never parks.
        row.NextAttemptUtc = nextAttempt;
        row.LastError = $"Blocked by outbox row #{blockerId}{(parked ? " (parked)" : "")}.";
    }

    private static TupleKey ToTupleKey(SealedFgaOutboxEntry entry)
        => new() {
            // Guaranteed non-null for WriteTuple/DeleteTuple rows (see SealedFgaOutboxEntry factories).
            User = entry.TupleUser!,
            Relation = entry.TupleRelation!,
            Object = entry.TupleObject!,
        };

    internal static TimeSpan ComputeBackoff(int attempts) {
        // Exponential backoff (2^attempts seconds), capped.
        var seconds = Math.Min(MaxBackoffSeconds, Math.Pow(2, attempts));
        return TimeSpan.FromSeconds(seconds);
    }
}
