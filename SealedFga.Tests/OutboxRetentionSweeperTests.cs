using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using SealedFga.Fga.Outbox;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>
///     Retention sweep on SQLite: expired processed rows are deleted, but never rows that
///     are still newest-wins witnesses for unprocessed rows; parked tuple rows with a processed
///     witness are marked superseded; parked fences are never auto-cleared.
/// </summary>
public class OutboxRetentionSweeperTests {
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private const int MaxAttempts = 5;

    private static (FakeTimeProvider Time, DateTime Now, DateTime Expired) Clock() {
        var time = new FakeTimeProvider();
        var now = time.GetUtcNow().UtcDateTime;
        return (time, now, now.AddDays(-8)); // older than the 7-day window
    }

    private static SealedFgaOutboxEntry Tuple(
        SealedFgaOutboxOperationType op, string user, string rel, string obj,
        DateTime? processed = null, int attempts = 0
    ) => new() {
        OperationType = op, TupleUser = user, TupleRelation = rel, TupleObject = obj,
        ProcessedAtUtc = processed, Attempts = attempts,
    };

    [Fact]
    public async Task Deletes_expired_processed_rows_and_keeps_recent_and_pending_ones() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        var (time, now, expired) = Clock();
        ctx.Outbox.AddRange(
            Tuple(SealedFgaOutboxOperationType.WriteTuple, "u:1", "r", "o:1", processed: expired),
            Tuple(SealedFgaOutboxOperationType.WriteTuple, "u:2", "r", "o:2", processed: now.AddDays(-1)),
            Tuple(SealedFgaOutboxOperationType.WriteTuple, "u:3", "r", "o:3") // pending
        );
        await ctx.SaveChangesAsync();

        var swept = await SealedFgaOutboxRetentionSweeper.SweepAsync(ctx, Retention, MaxAttempts, time);

        swept.ShouldBe(1);
        ctx.ChangeTracker.Clear();
        ctx.Outbox.Select(e => e.TupleUser).ToList().ShouldBe(["u:2", "u:3"], ignoreOrder: true);
    }

    [Fact]
    public async Task Null_retention_period_is_a_noop() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        var (time, _, expired) = Clock();
        ctx.Outbox.Add(Tuple(SealedFgaOutboxOperationType.WriteTuple, "u:1", "r", "o:1", processed: expired));
        await ctx.SaveChangesAsync();

        (await SealedFgaOutboxRetentionSweeper.SweepAsync(ctx, null, MaxAttempts, time)).ShouldBe(0);
        ctx.Outbox.Count().ShouldBe(1);
    }

    [Fact]
    public async Task Witness_for_parked_row_is_protected_then_released_by_parked_row_cleanup() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        var (time, now, expired) = Clock();
        // Id 1: parked Del(X) (unprocessed forever); Id 2: processed newer Write(X), expired.
        ctx.Outbox.AddRange(
            Tuple(SealedFgaOutboxOperationType.DeleteTuple, "u:x", "r", "o:x", attempts: MaxAttempts),
            Tuple(SealedFgaOutboxOperationType.WriteTuple, "u:x", "r", "o:x", processed: expired)
        );
        await ctx.SaveChangesAsync();

        await SealedFgaOutboxRetentionSweeper.SweepAsync(ctx, Retention, MaxAttempts, time);

        ctx.ChangeTracker.Clear();
        var rows = ctx.Outbox.OrderBy(e => e.Id).ToList();
        // The parked row is superseded by the witness → marked processed (cleanup)...
        rows[0].ProcessedAtUtc.ShouldBe(now);
        rows[0].LastError.ShouldNotBeNull();
        rows[0].LastError!.ShouldContain("Superseded");
        // ...which releases the witness for deletion within the same sweep.
        rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Expired_witness_with_older_backed_off_same_key_row_is_kept() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        var (time, now, expired) = Clock();
        // Id 1: backed-off (NOT parked) Del(X); Id 2: processed Write(X), expired — still needed
        // as the newest-wins witness when row 1 retries.
        var backedOff = Tuple(SealedFgaOutboxOperationType.DeleteTuple, "u:x", "r", "o:x", attempts: 1);
        backedOff.NextAttemptUtc = now.AddMinutes(5);
        ctx.Outbox.AddRange(
            backedOff,
            Tuple(SealedFgaOutboxOperationType.WriteTuple, "u:x", "r", "o:x", processed: expired)
        );
        await ctx.SaveChangesAsync();

        var swept = await SealedFgaOutboxRetentionSweeper.SweepAsync(ctx, Retention, MaxAttempts, time);

        swept.ShouldBe(0);
        ctx.ChangeTracker.Clear();
        ctx.Outbox.Count().ShouldBe(2);
    }

    [Fact]
    public async Task Expired_processed_fence_with_older_unprocessed_same_entity_row_is_kept() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        var (time, now, expired) = Clock();
        // Id 1: backed-off write referencing entity o:e; Id 2: processed fence for o:e, expired —
        // still needed as row 1's fence-supersession witness.
        var pending = Tuple(SealedFgaOutboxOperationType.WriteTuple, "u:1", "r", "o:e", attempts: 1);
        pending.NextAttemptUtc = now.AddMinutes(5);
        ctx.Outbox.AddRange(pending, new SealedFgaOutboxEntry {
            OperationType = SealedFgaOutboxOperationType.DeleteAllForObject,
            TargetId = "o:e", TypeName = "o", ProcessedAtUtc = expired,
        });
        await ctx.SaveChangesAsync();

        (await SealedFgaOutboxRetentionSweeper.SweepAsync(ctx, Retention, MaxAttempts, time)).ShouldBe(0);
        ctx.ChangeTracker.Clear();
        ctx.Outbox.Count().ShouldBe(2);
    }

    [Fact]
    public async Task Parked_fence_is_never_auto_cleared() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        var (time, _, _) = Clock();
        ctx.Outbox.Add(new SealedFgaOutboxEntry {
            OperationType = SealedFgaOutboxOperationType.DeleteAllForObject,
            TargetId = "o:e", TypeName = "o", Attempts = MaxAttempts,
        });
        await ctx.SaveChangesAsync();

        (await SealedFgaOutboxRetentionSweeper.SweepAsync(ctx, Retention, MaxAttempts, time)).ShouldBe(0);
        ctx.ChangeTracker.Clear();
        ctx.Outbox.Single().ProcessedAtUtc.ShouldBeNull();
    }

    [Fact]
    public async Task InMemory_fallback_applies_the_same_rules() {
        using var ctx = TestDbContext.CreateInMemory();
        var (time, _, expired) = Clock();
        ctx.Outbox.AddRange(
            Tuple(SealedFgaOutboxOperationType.WriteTuple, "u:1", "r", "o:1", processed: expired),
            Tuple(SealedFgaOutboxOperationType.DeleteTuple, "u:x", "r", "o:x", attempts: MaxAttempts),
            Tuple(SealedFgaOutboxOperationType.WriteTuple, "u:x", "r", "o:x", processed: expired)
        );
        await ctx.SaveChangesAsync();

        await SealedFgaOutboxRetentionSweeper.SweepAsync(ctx, Retention, MaxAttempts, time);

        // Expired unrelated row deleted; parked row cleaned up (superseded by its witness); the
        // witness itself released and deleted.
        ctx.Outbox.Count().ShouldBe(1);
        ctx.Outbox.Single().ProcessedAtUtc.ShouldNotBeNull();
        ctx.Outbox.Single().LastError!.ShouldContain("Superseded");
    }
}
