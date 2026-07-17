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
///     Exercises the single-drainer lease against real SQLite (the same relational
///     ExecuteUpdate path Postgres uses), with a fake clock for expiry.
/// </summary>
public class OutboxLeaseManagerTests {
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task First_acquire_bootstraps_the_lease_row_and_wins() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        var time = new FakeTimeProvider();

        var acquired = await SealedFgaOutboxLeaseManager.TryAcquireOrRenewAsync(ctx, "replica-a", Ttl, time);

        acquired.ShouldBeTrue();
        var lease = ctx.Set<SealedFgaOutboxLease>().Single();
        lease.HolderId.ShouldBe("replica-a");
        lease.ExpiresAtUtc.ShouldBe(time.GetUtcNow().UtcDateTime.Add(Ttl));
    }

    [Fact]
    public async Task Holder_renews_while_valid_and_extends_expiry() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        var time = new FakeTimeProvider();

        (await SealedFgaOutboxLeaseManager.TryAcquireOrRenewAsync(ctx, "replica-a", Ttl, time)).ShouldBeTrue();
        time.Advance(TimeSpan.FromSeconds(5));
        (await SealedFgaOutboxLeaseManager.TryAcquireOrRenewAsync(ctx, "replica-a", Ttl, time)).ShouldBeTrue();

        ctx.ChangeTracker.Clear();
        var lease = ctx.Set<SealedFgaOutboxLease>().Single();
        lease.ExpiresAtUtc.ShouldBe(time.GetUtcNow().UtcDateTime.Add(Ttl));
    }

    [Fact]
    public async Task Second_replica_is_denied_while_lease_is_held() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        using var ctxB = TestDbContext.CreateSqliteOn(conn);
        var time = new FakeTimeProvider();

        (await SealedFgaOutboxLeaseManager.TryAcquireOrRenewAsync(ctx, "replica-a", Ttl, time)).ShouldBeTrue();
        time.Advance(TimeSpan.FromSeconds(5)); // still within the TTL
        (await SealedFgaOutboxLeaseManager.TryAcquireOrRenewAsync(ctxB, "replica-b", Ttl, time)).ShouldBeFalse();
    }

    [Fact]
    public async Task Second_replica_takes_over_after_expiry() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        using var ctxB = TestDbContext.CreateSqliteOn(conn);
        var time = new FakeTimeProvider();

        (await SealedFgaOutboxLeaseManager.TryAcquireOrRenewAsync(ctx, "replica-a", Ttl, time)).ShouldBeTrue();
        time.Advance(Ttl + TimeSpan.FromSeconds(1)); // leader "crashed": no renewal
        (await SealedFgaOutboxLeaseManager.TryAcquireOrRenewAsync(ctxB, "replica-b", Ttl, time)).ShouldBeTrue();

        // The previous holder must not silently win it back while B's lease is valid.
        (await SealedFgaOutboxLeaseManager.TryAcquireOrRenewAsync(ctx, "replica-a", Ttl, time)).ShouldBeFalse();
    }

    [Fact]
    public async Task Acquire_works_against_a_preexisting_expired_row() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        var time = new FakeTimeProvider();
        ctx.Set<SealedFgaOutboxLease>().Add(new SealedFgaOutboxLease {
            Name = SealedFgaOutboxLeaseManager.LeaseName,
            HolderId = "dead-replica",
            ExpiresAtUtc = time.GetUtcNow().UtcDateTime.AddMinutes(-5),
        });
        await ctx.SaveChangesAsync();

        (await SealedFgaOutboxLeaseManager.TryAcquireOrRenewAsync(ctx, "replica-a", Ttl, time)).ShouldBeTrue();
        ctx.ChangeTracker.Clear();
        ctx.Set<SealedFgaOutboxLease>().Single().HolderId.ShouldBe("replica-a");
    }
}
