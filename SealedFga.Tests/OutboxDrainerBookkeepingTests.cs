using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using OpenFga.Sdk.Client;
using SealedFga.Fga;
using SealedFga.Fga.Outbox;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>
///     Drainer bookkeeping on SQLite for the plan outcomes that involve no OpenFGA call at all
///     (supersession and deferral): the service below points at a dead endpoint, so any
///     accidental apply attempt would fail the test loudly.
/// </summary>
public class OutboxDrainerBookkeepingTests {
    private static SealedFgaService DeadService() => new(
        new OpenFgaClient(new ClientConfiguration { ApiUrl = "http://127.0.0.1:1" }),
        Microsoft.Extensions.Options.Options.Create(new SealedFgaOptions())
    );

    [Fact]
    public async Task Superseded_row_is_marked_processed_without_an_fga_call() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        var time = new FakeTimeProvider();

        // Id 1: pending Del(X) (a revived retry); Id 2: newer Write(X), already processed.
        ctx.Outbox.Add(SealedFgaOutboxEntry.ForDelete("u:x", "r", "o:x"));
        var processed = SealedFgaOutboxEntry.ForWrite("u:x", "r", "o:x");
        processed.ProcessedAtUtc = time.GetUtcNow().UtcDateTime.AddMinutes(-1);
        ctx.Outbox.Add(processed);
        await ctx.SaveChangesAsync();

        var changed = await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, DeadService(), 10, 5, time);

        changed.ShouldBe(1);
        ctx.ChangeTracker.Clear();
        var row = ctx.Outbox.Single(e => e.Id == 1);
        row.ProcessedAtUtc.ShouldNotBeNull();
        row.Attempts.ShouldBe(0);
        row.LastError.ShouldBe("Superseded by outbox row #2.");
    }

    [Fact]
    public async Task Deferred_row_gets_blockers_retry_time_and_no_attempts_bump() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        var time = new FakeTimeProvider();
        var fenceRetryAt = time.GetUtcNow().UtcDateTime.AddMinutes(2);

        // Id 1: backed-off fence for entity o:e; Id 2: eligible write referencing o:e.
        var fence = SealedFgaOutboxEntry.ForDeleteAllForObject("o:e", "o");
        fence.Attempts = 1;
        fence.NextAttemptUtc = fenceRetryAt;
        ctx.Outbox.Add(fence);
        ctx.Outbox.Add(SealedFgaOutboxEntry.ForWrite("u:1", "r", "o:e"));
        await ctx.SaveChangesAsync();

        var changed = await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, DeadService(), 10, 5, time);

        changed.ShouldBe(1);
        ctx.ChangeTracker.Clear();
        var row = ctx.Outbox.Single(e => e.Id == 2);
        row.ProcessedAtUtc.ShouldBeNull();
        row.Attempts.ShouldBe(0); // deferral is not failure
        row.NextAttemptUtc.ShouldBe(fenceRetryAt);
        row.LastError.ShouldBe("Blocked by outbox row #1.");

        // The deferred row is now ineligible: the next immediate pass has nothing to do.
        (await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, DeadService(), 10, 5, time)).ShouldBe(0);
    }

    [Fact]
    public async Task Row_blocked_by_parked_fence_is_deferred_by_max_backoff_and_never_parks() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        var time = new FakeTimeProvider();

        var parkedFence = SealedFgaOutboxEntry.ForDeleteAllForObject("o:e", "o");
        parkedFence.Attempts = 5; // == maxAttempts → parked
        ctx.Outbox.Add(parkedFence);
        ctx.Outbox.Add(SealedFgaOutboxEntry.ForWrite("u:1", "r", "o:e"));
        await ctx.SaveChangesAsync();

        var changed = await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, DeadService(), 10, 5, time);

        changed.ShouldBe(1);
        ctx.ChangeTracker.Clear();
        var row = ctx.Outbox.Single(e => e.Id == 2);
        row.Attempts.ShouldBe(0);
        row.NextAttemptUtc.ShouldBe(time.GetUtcNow().UtcDateTime.Add(SealedFgaOutboxDrainer.MaxBackoff));
        row.LastError.ShouldBe("Blocked by outbox row #1 (parked).");
    }

    [Fact]
    public async Task Row_superseded_by_processed_fence_is_not_applied() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        var time = new FakeTimeProvider();

        // Id 1: pending write for entity o:e (late-visible under commit skew); Id 2: the entity's
        // purge fence, already processed. Applying row 1 would resurrect a deleted entity's tuple.
        ctx.Outbox.Add(SealedFgaOutboxEntry.ForWrite("u:1", "r", "o:e"));
        var fence = SealedFgaOutboxEntry.ForDeleteAllForObject("o:e", "o");
        fence.ProcessedAtUtc = time.GetUtcNow().UtcDateTime.AddMinutes(-1);
        ctx.Outbox.Add(fence);
        await ctx.SaveChangesAsync();

        var changed = await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, DeadService(), 10, 5, time);

        changed.ShouldBe(1);
        ctx.ChangeTracker.Clear();
        var row = ctx.Outbox.Single(e => e.Id == 1);
        row.ProcessedAtUtc.ShouldNotBeNull();
        row.LastError.ShouldBe("Superseded by outbox row #2.");
    }
}
