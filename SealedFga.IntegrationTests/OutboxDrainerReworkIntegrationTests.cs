using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SealedFga.Fga.Outbox;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.IntegrationTests;

/// <summary>
///     End-to-end tests of the drainer semantics (newest-wins + fences, batching)
///     against a real OpenFGA server with a SQLite-backed outbox.
/// </summary>
[Collection(OpenFgaCollection.Name)]
[Trait("Category", "Integration")]
public class OutboxDrainerReworkIntegrationTests(OpenFgaFixture fga) {
    [Fact]
    public async Task F2_convergence_revived_old_delete_is_superseded_and_does_not_fire() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        var obj = $"testobject:{GuidOf("f2-conv")}";

        // Id 1: Del(X) that failed earlier and is backed off; Id 2: newer Write(X), eligible.
        var oldDelete = SealedFgaOutboxEntry.ForDelete("testuser:ivan", "can_view", obj);
        oldDelete.Attempts = 1;
        oldDelete.NextAttemptUtc = DateTime.UtcNow.AddMinutes(5);
        ctx.Outbox.Add(oldDelete);
        ctx.Outbox.Add(SealedFgaOutboxEntry.ForWrite("testuser:ivan", "can_view", obj));
        await ctx.SaveChangesAsync();

        // Pass 1 applies the newer write (the delete is ineligible).
        (await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, 10, 5)).ShouldBe(1);
        (await fga.Service.ListAllRelationsToObjectAsync(obj)).ShouldHaveSingleItem();

        // The old delete becomes eligible again (its retry fires last)...
        oldDelete.NextAttemptUtc = DateTime.UtcNow.AddMinutes(-1);
        await ctx.SaveChangesAsync();
        (await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, 10, 5)).ShouldBe(1);

        // ...but is superseded by the processed newer write: the tuple must survive.
        (await fga.Service.ListAllRelationsToObjectAsync(obj)).ShouldHaveSingleItem();
        ctx.ChangeTracker.Clear();
        var row = ctx.Outbox.OrderBy(e => e.Id).First();
        row.ProcessedAtUtc.ShouldNotBeNull();
        row.LastError!.ShouldStartWith("Superseded by outbox row");
    }

    [Fact]
    public async Task Fence_orders_write_purge_rewrite_within_one_pass() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        var obj = $"testobject:{GuidOf("fence-ord")}";

        ctx.Outbox.Add(SealedFgaOutboxEntry.ForWrite("testuser:judy", "can_view", obj));
        ctx.Outbox.Add(SealedFgaOutboxEntry.ForDeleteAllForObject(obj, "testobject"));
        ctx.Outbox.Add(SealedFgaOutboxEntry.ForWrite("testuser:judy", "can_view", obj));
        await ctx.SaveChangesAsync();

        var changed = await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, 10, 5);

        changed.ShouldBe(3);
        ctx.ChangeTracker.Clear();
        ctx.Outbox.ToList().ShouldAllBe(e => e.ProcessedAtUtc != null);
        // Write → purge → re-write: exactly the post-fence tuple must exist.
        (await fga.Service.ListAllRelationsToObjectAsync(obj)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Failed_fence_defers_later_same_entity_rows_but_unrelated_rows_proceed() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        var goodObj = $"testobject:{GuidOf("fence-good")}";

        // "not-a-tuple-string" (no type:id shape) makes the fence's Read call fail server-side.
        ctx.Outbox.Add(SealedFgaOutboxEntry.ForDeleteAllForObject("not-a-tuple-string", "testobject"));
        ctx.Outbox.Add(SealedFgaOutboxEntry.ForWrite("testuser:kate", "can_view", "not-a-tuple-string"));
        ctx.Outbox.Add(SealedFgaOutboxEntry.ForWrite("testuser:kate", "can_view", goodObj));
        await ctx.SaveChangesAsync();

        var changed = await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, 10, 5);

        changed.ShouldBe(3);
        ctx.ChangeTracker.Clear();
        var rows = ctx.Outbox.OrderBy(e => e.Id).ToList();
        rows[0].ProcessedAtUtc.ShouldBeNull(); // fence failed
        rows[0].Attempts.ShouldBe(1);
        rows[1].ProcessedAtUtc.ShouldBeNull(); // deferred behind the failed fence
        rows[1].Attempts.ShouldBe(0);
        rows[1].LastError.ShouldBe("Blocked by outbox row #1.");
        rows[2].ProcessedAtUtc.ShouldNotBeNull(); // unrelated row applied in the same pass
        (await fga.Service.ListAllRelationsToObjectAsync(goodObj)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Poison_tuple_is_isolated_and_chunk_mates_still_apply() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        var obj = $"testobject:{GuidOf("poison")}";

        // All three land in one write chunk; the middle one is rejected by the model
        // (unknown relation). Chunk-level failure must not park the two valid rows.
        ctx.Outbox.Add(SealedFgaOutboxEntry.ForWrite("testuser:liam", "can_view", obj));
        ctx.Outbox.Add(SealedFgaOutboxEntry.ForWrite("testuser:mia", "relation_not_in_model", obj));
        ctx.Outbox.Add(SealedFgaOutboxEntry.ForWrite("testuser:noah", "can_view", obj));
        await ctx.SaveChangesAsync();

        var changed = await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, 10, 5);

        changed.ShouldBe(3);
        ctx.ChangeTracker.Clear();
        var rows = ctx.Outbox.OrderBy(e => e.Id).ToList();
        rows[0].ProcessedAtUtc.ShouldNotBeNull();
        rows[1].ProcessedAtUtc.ShouldBeNull(); // the poison row alone keeps retrying
        rows[1].Attempts.ShouldBe(1);
        rows[2].ProcessedAtUtc.ShouldNotBeNull();
        (await fga.Service.ListAllRelationsToObjectAsync(obj)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Thousand_row_fanout_drains_in_one_batched_pass() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        var obj = $"testobject:{GuidOf("fanout-1k")}";
        const int count = 1000;

        for (var i = 0; i < count; i++) {
            ctx.Outbox.Add(SealedFgaOutboxEntry.ForWrite($"testuser:fan-{i}", "can_view", obj));
        }

        await ctx.SaveChangesAsync();

        var stopwatch = Stopwatch.StartNew();
        var changed = await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, count, 5);
        stopwatch.Stop();

        changed.ShouldBe(count);
        ctx.ChangeTracker.Clear();
        ctx.Outbox.Count(e => e.ProcessedAtUtc != null).ShouldBe(count);
        (await fga.Service.ListAllRelationsToObjectAsync(obj)).Count.ShouldBe(count);
        // Throughput sanity for bulk fan-outs: batched writes, not 1,000 HTTP round trips.
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
    }

    private static string GuidOf(string seed) {
        var bytes = new byte[16];
        var s = seed.PadRight(16, '_');
        for (var i = 0; i < 16; i++) {
            bytes[i] = (byte) s[i % s.Length];
        }

        return new Guid(bytes).ToString();
    }
}
