using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SealedFga.AuthModel;
using SealedFga.Fga;
using SealedFga.Fga.Outbox;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>
///     Unit tests for the end-of-request outbox wait — <see cref="SealedFgaOutboxPendingRows" />,
///     <see cref="SealedFgaOutboxTrackingInterceptor" /> and
///     <see cref="SealedFgaOutboxWaiter{TDbContext}" />. Together these give a caller
///     read-after-write: once the wait returns, the tuple authorizing the entity it just created has
///     been applied, so an immediate permission check on it succeeds.
/// </summary>
[Collection(GlobalCollection.Name)]
public class OutboxWaitTests {
    private sealed class TestObjectRelation(string val) : SealedFgaRelation(val), ISealedFgaRelation<TestObjectId>;

    private static readonly TestObjectRelation CanView = new("can_view");

    /// <summary>Short enough to keep the timeout tests fast, long enough not to be flaky.</summary>
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(300);

    private static IOptions<SealedFgaOptions> OptionsWith(Action<SealedFgaOptions>? configure = null) {
        var opts = new SealedFgaOptions {
            OutboxWaitTimeout = ShortTimeout,
            OutboxWaitPollInterval = TimeSpan.FromMilliseconds(10),
        };
        configure?.Invoke(opts);
        return Options.Create(opts);
    }

    #region Pending-row tracking

    [Fact]
    public void Take_returns_what_was_added_and_resets_the_scope() {
        var rows = new SealedFgaOutboxPendingRows();
        rows.Add([1L, 2L], maxRows: 10);
        rows.Add([3L], maxRows: 10);

        var (ids, overflowed) = rows.Take();

        ids.ShouldBe([1L, 2L, 3L]);
        overflowed.ShouldBeFalse();
        // A second wait in the same scope must only cover work enqueued since the first.
        rows.Take().RowIds.ShouldBeEmpty();
    }

    [Fact]
    public void Exceeding_the_cap_overflows_instead_of_growing() {
        var rows = new SealedFgaOutboxPendingRows();
        rows.Add([1L, 2L], maxRows: 3);
        rows.Add([3L, 4L], maxRows: 3);

        var (ids, overflowed) = rows.Take();

        overflowed.ShouldBeTrue();
        ids.ShouldBeEmpty();
    }

    [Fact]
    public void Overflow_is_cleared_by_taking() {
        var rows = new SealedFgaOutboxPendingRows();
        rows.Add([1L, 2L, 3L], maxRows: 2);
        rows.Take().Overflowed.ShouldBeTrue();

        rows.Add([4L], maxRows: 2);

        var (ids, overflowed) = rows.Take();
        overflowed.ShouldBeFalse();
        ids.ShouldBe([4L]);
    }

    [Fact]
    public void Committing_enqueued_rows_records_their_ids() {
        var rows = new SealedFgaOutboxPendingRows();
        var (ctx, conn) = TestDbContext.CreateSqliteWith(
            new SealedFgaOutboxTrackingInterceptor(rows, OptionsWith())
        );
        using var _ = conn;

        ctx.EnqueueFgaWrite(new TestUserId("alice"), CanView, TestObjectId.New());
        ctx.SaveChanges();

        var (ids, _) = rows.Take();
        ids.Count.ShouldBe(1);
        // Database-generated, so this also pins that ids are read after the save, not before.
        ids[0].ShouldBe(ctx.Outbox.Single().Id);
        ids[0].ShouldBeGreaterThan(0);
    }

    [Fact]
    public void A_save_that_enqueues_nothing_records_nothing() {
        var rows = new SealedFgaOutboxPendingRows();
        var (ctx, conn) = TestDbContext.CreateSqliteWith(
            new SealedFgaOutboxTrackingInterceptor(rows, OptionsWith())
        );
        using var _ = conn;

        ctx.Objects.Add(new TestObjectEntity { Id = TestObjectId.New() });
        ctx.SaveChanges();

        rows.Take().RowIds.ShouldBeEmpty();
    }

    #endregion

    #region Waiting

    [Fact]
    public async Task Waiting_with_nothing_pending_returns_immediately() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        var waiter = new SealedFgaOutboxWaiter<TestDbContext>(ctx, new SealedFgaOutboxPendingRows(), OptionsWith());

        (await waiter.WaitForPendingAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task Waiting_returns_once_the_rows_are_applied() {
        var rows = new SealedFgaOutboxPendingRows();
        var (ctx, conn) = TestDbContext.CreateSqliteWith(new SealedFgaOutboxTrackingInterceptor(rows, OptionsWith()));
        using var _ = conn;

        ctx.EnqueueFgaWrite(new TestUserId("alice"), CanView, TestObjectId.New());
        await ctx.SaveChangesAsync();

        // Stand in for the drainer: mark the rows applied while the wait is outstanding.
        var applier = Task.Run(async () => {
            await using var drainerCtx = TestDbContext.CreateSqliteOn(conn);
            var row = await drainerCtx.Outbox.SingleAsync();
            row.ProcessedAtUtc = DateTime.UtcNow;
            await drainerCtx.SaveChangesAsync();
        });

        var waiter = new SealedFgaOutboxWaiter<TestDbContext>(ctx, rows, OptionsWith());
        (await waiter.WaitForPendingAsync()).ShouldBeTrue();
        await applier;
    }

    [Fact]
    public async Task Waiting_times_out_rather_than_hanging_when_nothing_drains() {
        var rows = new SealedFgaOutboxPendingRows();
        var (ctx, conn) = TestDbContext.CreateSqliteWith(new SealedFgaOutboxTrackingInterceptor(rows, OptionsWith()));
        using var _ = conn;

        ctx.EnqueueFgaWrite(new TestUserId("alice"), CanView, TestObjectId.New());
        await ctx.SaveChangesAsync();

        var waiter = new SealedFgaOutboxWaiter<TestDbContext>(ctx, rows, OptionsWith());

        // False, not an exception: the write itself succeeded and the rows stay queued.
        (await waiter.WaitForPendingAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task An_overflowed_scope_is_not_waited_on() {
        var rows = new SealedFgaOutboxPendingRows();
        var options = OptionsWith(o => o.OutboxWaitMaxRows = 1);
        var (ctx, conn) = TestDbContext.CreateSqliteWith(new SealedFgaOutboxTrackingInterceptor(rows, options));
        using var _ = conn;

        ctx.EnqueueFgaWrite(new TestUserId("alice"), CanView, TestObjectId.New());
        ctx.EnqueueFgaWrite(new TestUserId("bob"), CanView, TestObjectId.New());
        await ctx.SaveChangesAsync();

        var waiter = new SealedFgaOutboxWaiter<TestDbContext>(ctx, rows, options);
        (await waiter.WaitForPendingAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Waiting_is_skipped_when_no_drainer_runs() {
        var rows = new SealedFgaOutboxPendingRows();
        var options = OptionsWith(o => o.RunOutboxDrainer = false);
        var (ctx, conn) = TestDbContext.CreateSqliteWith(new SealedFgaOutboxTrackingInterceptor(rows, options));
        using var _ = conn;

        ctx.EnqueueFgaWrite(new TestUserId("alice"), CanView, TestObjectId.New());
        await ctx.SaveChangesAsync();

        // Nothing would ever mark these processed, so waiting could only burn the full timeout on
        // every call — consumers driving the drainer themselves await their own drain instead.
        var start = DateTime.UtcNow;
        var waiter = new SealedFgaOutboxWaiter<TestDbContext>(ctx, rows, options);
        (await waiter.WaitForPendingAsync()).ShouldBeTrue();
        (DateTime.UtcNow - start).ShouldBeLessThan(ShortTimeout);
    }

    #endregion

    #region Backlog waiting

    [Fact]
    public async Task Waiting_for_an_empty_backlog_returns_immediately() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        var waiter = new SealedFgaOutboxWaiter<TestDbContext>(ctx, new SealedFgaOutboxPendingRows(), OptionsWith());

        (await waiter.WaitForBacklogAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task The_backlog_wait_covers_rows_this_scope_never_enqueued() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        // Enqueued by "someone else" — a queued background task depends on tuple changes an earlier
        // request committed, and has nothing of its own to wait for.
        await using (var other = TestDbContext.CreateSqliteOn(conn)) {
            other.EnqueueFgaWrite(new TestUserId("alice"), CanView, TestObjectId.New());
            await other.SaveChangesAsync();
        }

        var waiter = new SealedFgaOutboxWaiter<TestDbContext>(ctx, new SealedFgaOutboxPendingRows(), OptionsWith());

        // Scope-local waiting would sail straight past this; the backlog wait must not.
        (await waiter.WaitForBacklogAsync()).ShouldBeFalse();

        await using (var drainer = TestDbContext.CreateSqliteOn(conn)) {
            var row = await drainer.Outbox.SingleAsync();
            row.ProcessedAtUtc = DateTime.UtcNow;
            await drainer.SaveChangesAsync();
        }

        (await waiter.WaitForBacklogAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task Permanently_parked_rows_do_not_hold_the_backlog_wait_forever() {
        var options = OptionsWith(o => o.OutboxMaxAttempts = 3);
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        ctx.EnqueueFgaWrite(new TestUserId("alice"), CanView, TestObjectId.New());
        await ctx.SaveChangesAsync();
        var parked = await ctx.Outbox.SingleAsync();
        parked.Attempts = 3;
        await ctx.SaveChangesAsync();

        var waiter = new SealedFgaOutboxWaiter<TestDbContext>(ctx, new SealedFgaOutboxPendingRows(), options);

        // Nothing will ever apply this row, so waiting on it could only ever burn the timeout.
        (await waiter.WaitForBacklogAsync()).ShouldBeTrue();
    }

    #endregion
}
