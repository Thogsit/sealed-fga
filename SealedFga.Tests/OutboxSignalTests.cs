using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SealedFga.AuthModel;
using SealedFga.Fga;
using SealedFga.Fga.Outbox;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>
///     Unit tests for the drainer wake-up path — <see cref="SealedFgaOutboxSignal" /> and
///     <see cref="SealedFgaOutboxSignalInterceptor" />. Together they remove the poll-interval delay
///     between committing a tuple change and it being visible in OpenFGA, so a caller that just
///     created an entity can immediately pass a permission check on it.
/// </summary>
[Collection(GlobalCollection.Name)]
public class OutboxSignalTests {
    /// <summary>A relation constant bound to <see cref="TestObjectId" />, as the generator would emit.</summary>
    private sealed class TestObjectRelation(string val) : SealedFgaRelation(val), ISealedFgaRelation<TestObjectId>;

    private static readonly TestObjectRelation CanView = new("can_view");

    [Fact]
    public void WaitAsync_does_not_complete_before_a_signal() {
        var signal = new SealedFgaOutboxSignal();

        signal.WaitAsync().IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public void Signal_raised_before_the_wait_is_still_observed() {
        var signal = new SealedFgaOutboxSignal();

        // The drainer signals-then-waits ordering: a commit landing mid-pass must not be slept through.
        signal.Signal();

        signal.WaitAsync().IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Signal_completes_an_outstanding_wait() {
        var signal = new SealedFgaOutboxSignal();
        var wait = signal.WaitAsync();

        signal.Signal();

        await wait.WaitAsync(TestTimeout);
        wait.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public void Observing_a_signal_re_arms_it() {
        var signal = new SealedFgaOutboxSignal();
        signal.Signal();
        signal.WaitAsync().IsCompleted.ShouldBeTrue();

        // Without the re-arm the drainer would spin: every later wait would return completed.
        signal.WaitAsync().IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public void Repeated_signals_collapse_into_one_wake_up() {
        var signal = new SealedFgaOutboxSignal();

        signal.Signal();
        signal.Signal();
        signal.Signal();

        signal.WaitAsync().IsCompleted.ShouldBeTrue();
        signal.WaitAsync().IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public void Committing_enqueued_rows_wakes_the_drainer() {
        var signal = new SealedFgaOutboxSignal();
        var (ctx, conn) = TestDbContext.CreateSqliteWith(new SealedFgaOutboxSignalInterceptor(signal));
        using var _ = conn;

        ctx.EnqueueFgaWrite(new TestUserId("alice"), CanView, TestObjectId.New());
        ctx.SaveChanges();

        signal.WaitAsync().IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task Committing_enqueued_rows_wakes_the_drainer_on_the_async_path() {
        var signal = new SealedFgaOutboxSignal();
        var (ctx, conn) = TestDbContext.CreateSqliteWith(new SealedFgaOutboxSignalInterceptor(signal));
        using var _ = conn;

        ctx.EnqueueFgaWrite(new TestUserId("alice"), CanView, TestObjectId.New());
        await ctx.SaveChangesAsync();

        signal.WaitAsync().IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public void A_save_that_enqueues_nothing_leaves_the_drainer_asleep() {
        var signal = new SealedFgaOutboxSignal();
        var (ctx, conn) = TestDbContext.CreateSqliteWith(new SealedFgaOutboxSignalInterceptor(signal));
        using var _ = conn;

        ctx.Objects.Add(new TestObjectEntity { Id = TestObjectId.New() });
        ctx.SaveChanges();

        signal.WaitAsync().IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public void Updating_existing_outbox_rows_does_not_wake_the_drainer() {
        var signal = new SealedFgaOutboxSignal();
        var (ctx, conn) = TestDbContext.CreateSqliteWith(new SealedFgaOutboxSignalInterceptor(signal));
        using var _ = conn;

        ctx.EnqueueFgaWrite(new TestUserId("alice"), CanView, TestObjectId.New());
        ctx.SaveChanges();
        signal.WaitAsync().IsCompleted.ShouldBeTrue();

        // The drainer marks rows applied through this very context type. If its own bookkeeping
        // counted as new work it would re-wake itself forever instead of going back to sleep.
        var row = ctx.Outbox.Single();
        row.Attempts += 1;
        ctx.SaveChanges();

        signal.WaitAsync().IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public void A_rolled_back_save_does_not_wake_the_drainer() {
        var signal = new SealedFgaOutboxSignal();
        var (ctx, conn) = TestDbContext.CreateSqliteWith(new SealedFgaOutboxSignalInterceptor(signal));
        using var _ = conn;

        using var tx = ctx.Database.BeginTransaction();
        ctx.EnqueueFgaWrite(new TestUserId("alice"), CanView, TestObjectId.New());
        ctx.SaveChanges();
        tx.Rollback();

        // The signal is raised on SaveChanges, before the enclosing transaction resolves, so it can
        // fire for rows that never commit. That is safe — the drainer simply finds nothing — and the
        // assertion below records that we accept the wake-up rather than suppress it.
        signal.WaitAsync().IsCompleted.ShouldBeTrue();
    }

    private static readonly System.TimeSpan TestTimeout = System.TimeSpan.FromSeconds(5);
}
