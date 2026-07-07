using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using OpenFga.Sdk.Model;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     Applies pending <see cref="SealedFgaOutboxEntry" /> rows to OpenFGA. Contains no scheduling of
///     its own; the hosted service (<c>SealedFgaOutboxHostedService</c>) drives it on a loop.
/// </summary>
public static class SealedFgaOutboxDrainer {
    /// <summary>The largest backoff between retries, in seconds.</summary>
    private const int MaxBackoffSeconds = 300;

    /// <summary>
    ///     Processes up to <paramref name="batchSize" /> eligible pending rows, in strict <c>Id</c>
    ///     order, applying each to OpenFGA. Successful rows are marked processed; failing rows have
    ///     their attempt count and next-retry time bumped. Runs in the caller's DI scope.
    /// </summary>
    /// <returns>The number of rows successfully applied in this pass.</returns>
    public static async Task<int> DrainOnceAsync(
        DbContext context,
        SealedFgaService fgaService,
        int batchSize,
        int maxAttempts,
        CancellationToken ct = default
    ) {
        var now = DateTime.UtcNow;
        var pending = await context.Set<SealedFgaOutboxEntry>()
                                   .Where(e => e.ProcessedAtUtc == null
                                               && e.Attempts < maxAttempts
                                               && (e.NextAttemptUtc == null || e.NextAttemptUtc <= now)
                                    )
                                   .OrderBy(e => e.Id)
                                   .Take(batchSize)
                                   .ToListAsync(ct);

        if (pending.Count == 0) {
            return 0;
        }

        var processed = 0;
        foreach (var entry in pending) {
            try {
                await ApplyAsync(entry, fgaService, ct);
                entry.ProcessedAtUtc = DateTime.UtcNow;
                entry.LastError = null;
                processed++;
            } catch (Exception ex) {
                // Keep the row pending; it will be retried after the backoff window.
                entry.Attempts++;
                entry.LastError = ex.Message;
                entry.NextAttemptUtc = DateTime.UtcNow.Add(ComputeBackoff(entry.Attempts));
            }
        }

        await context.SaveChangesAsync(ct);
        return processed;
    }

    private static Task ApplyAsync(SealedFgaOutboxEntry entry, SealedFgaService fgaService, CancellationToken ct) {
        switch (entry.OperationType) {
            case SealedFgaOutboxOperationType.WriteTuple:
                return fgaService.SafeWriteTupleAsync([ToTupleKey(entry)], ct);
            case SealedFgaOutboxOperationType.DeleteTuple:
                return fgaService.SafeDeleteTupleAsync([ToTupleKey(entry)], ct);
            case SealedFgaOutboxOperationType.DeleteAllForObject:
                return fgaService.DeleteAllRelationsForRawObjectAsync(entry.TargetId!, entry.TypeName!, ct);
            case SealedFgaOutboxOperationType.ModifyId:
                return fgaService.ModifyIdAsync(entry.TargetId!, entry.NewTargetId!, entry.TypeName!, ct);
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(entry),
                    entry.OperationType,
                    "Unknown outbox operation type."
                );
        }
    }

    private static TupleKey ToTupleKey(SealedFgaOutboxEntry entry)
        => new() {
            // Guaranteed non-null for WriteTuple/DeleteTuple rows (see SealedFgaOutboxEntry factories).
            User = entry.TupleUser!,
            Relation = entry.TupleRelation!,
            Object = entry.TupleObject!,
        };

    private static TimeSpan ComputeBackoff(int attempts) {
        // Exponential backoff (2^attempts seconds), capped.
        var seconds = Math.Min(MaxBackoffSeconds, Math.Pow(2, attempts));
        return TimeSpan.FromSeconds(seconds);
    }
}
