using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using SealedFga.Fga.Outbox;
using SealedFga.Tests.Support;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace SealedFga.IntegrationTests;

/// <summary>
///     Spins up a PostgreSQL container hosting the outbox tables,
///     to verify the relational code paths — lease ExecuteUpdate, sweep ExecuteDelete with the
///     witness-protection subqueries, and the drainer's blocker/witness queries — against real
///     Npgsql SQL translation.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime {
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    public async Task InitializeAsync() {
        await _container.StartAsync();
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public TestDbContext CreateContext() {
        var options = new DbContextOptionsBuilder<TestDbContext>()
                     .UseNpgsql(_container.GetConnectionString())
                     .Options;
        return new TestDbContext(options);
    }

    /// <summary>The database is shared per fixture; each test starts from empty outbox tables.</summary>
    public async Task<TestDbContext> CreateCleanContextAsync() {
        var ctx = CreateContext();
        await ctx.Outbox.ExecuteDeleteAsync();
        await ctx.Set<SealedFgaOutboxLease>().ExecuteDeleteAsync();
        return ctx;
    }
}

[Collection(OpenFgaCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresOutboxTests(OpenFgaFixture fga, PostgresFixture pg)
    : IClassFixture<PostgresFixture> {
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Schema_creation_produces_outbox_indexes_and_lease_table() {
        await using var ctx = pg.CreateContext();

        var indexes = await ctx.Database
                               .SqlQueryRaw<string>("""SELECT indexname AS "Value" FROM pg_indexes WHERE tablename = 'Outbox'""")
                               .ToListAsync();

        // PK + the three drainer indexes (claim scan, tuple key, fence lookups).
        indexes.Count.ShouldBeGreaterThanOrEqualTo(4);
        (await ctx.Set<SealedFgaOutboxLease>().CountAsync()).ShouldBeGreaterThanOrEqualTo(0); // table exists
    }

    [Fact]
    public async Task Lease_contention_exactly_one_leader_and_takeover_after_expiry() {
        await using var ctx = await pg.CreateCleanContextAsync();
        await using var ctxB = pg.CreateContext();
        var time = new FakeTimeProvider();

        var a = await SealedFgaOutboxLeaseManager.TryAcquireOrRenewAsync(ctx, "replica-a", Ttl, time);
        var b = await SealedFgaOutboxLeaseManager.TryAcquireOrRenewAsync(ctxB, "replica-b", Ttl, time);
        a.ShouldBeTrue();
        b.ShouldBeFalse();

        // Leader keeps renewing → B stays out.
        time.Advance(TimeSpan.FromSeconds(10));
        (await SealedFgaOutboxLeaseManager.TryAcquireOrRenewAsync(ctx, "replica-a", Ttl, time)).ShouldBeTrue();
        (await SealedFgaOutboxLeaseManager.TryAcquireOrRenewAsync(ctxB, "replica-b", Ttl, time)).ShouldBeFalse();

        // Leader "crashes" (stops renewing) → B takes over once the TTL lapses.
        time.Advance(Ttl + TimeSpan.FromSeconds(1));
        (await SealedFgaOutboxLeaseManager.TryAcquireOrRenewAsync(ctxB, "replica-b", Ttl, time)).ShouldBeTrue();
        (await SealedFgaOutboxLeaseManager.TryAcquireOrRenewAsync(ctx, "replica-a", Ttl, time)).ShouldBeFalse();
    }

    [Fact]
    public async Task Only_the_leaseholder_drains_and_the_backlog_converges() {
        await using var ctx = await pg.CreateCleanContextAsync();
        await using var ctxB = pg.CreateContext();
        var time = new FakeTimeProvider();
        var obj = $"testobject:{Guid.NewGuid()}";

        ctx.Outbox.Add(SealedFgaOutboxEntry.ForWrite("testuser:pg-a", "can_view", obj));
        ctx.Outbox.Add(SealedFgaOutboxEntry.ForWrite("testuser:pg-b", "can_view", obj));
        await ctx.SaveChangesAsync();

        // Replica A holds the lease and drains; replica B is denied and must not drain.
        (await SealedFgaOutboxLeaseManager.TryAcquireOrRenewAsync(ctx, "replica-a", Ttl, time)).ShouldBeTrue();
        (await SealedFgaOutboxLeaseManager.TryAcquireOrRenewAsync(ctxB, "replica-b", Ttl, time)).ShouldBeFalse();

        var changed = await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, 10, 5);

        changed.ShouldBe(2);
        (await ctx.Outbox.AsNoTracking().CountAsync(e => e.ProcessedAtUtc != null)).ShouldBe(2);
        (await fga.Service.ListAllRelationsToObjectAsync(obj)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Drainer_blocker_and_witness_queries_translate_on_postgres() {
        await using var ctx = await pg.CreateCleanContextAsync();
        var time = new FakeTimeProvider();
        var now = time.GetUtcNow().UtcDateTime;

        // An eligible fence claimed together with an older backed-off USERSET tuple row for the
        // same entity — exercises the StartsWith blocker predicate in real SQL. Plus a pending
        // delete superseded by a newer processed write (witness query).
        var backedOffUserset = SealedFgaOutboxEntry.ForWrite("testuser:pg-c#member", "Member", "testparent:1");
        backedOffUserset.Attempts = 1;
        backedOffUserset.NextAttemptUtc = now.AddMinutes(5);
        ctx.Outbox.Add(backedOffUserset);
        ctx.Outbox.Add(SealedFgaOutboxEntry.ForDeleteAllForObject("testuser:pg-c", "testuser"));
        ctx.Outbox.Add(SealedFgaOutboxEntry.ForDelete("u:x", "r", "o:x"));
        var witness = SealedFgaOutboxEntry.ForWrite("u:x", "r", "o:x");
        witness.ProcessedAtUtc = now.AddMinutes(-1);
        ctx.Outbox.Add(witness);
        await ctx.SaveChangesAsync();

        var changed = await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, 10, 5, time);

        // Fence deferred behind the backed-off userset row; delete superseded by the witness.
        changed.ShouldBe(2);
        var rows = await ctx.Outbox.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        rows[1].ProcessedAtUtc.ShouldBeNull();
        rows[1].Attempts.ShouldBe(0);
        rows[1].LastError!.ShouldStartWith("Blocked by outbox row");
        rows[2].ProcessedAtUtc.ShouldNotBeNull();
        rows[2].LastError!.ShouldStartWith("Superseded by outbox row");
    }

    [Fact]
    public async Task Retention_sweep_translates_on_postgres_including_witness_protection() {
        await using var ctx = await pg.CreateCleanContextAsync();
        var time = new FakeTimeProvider();
        var now = time.GetUtcNow().UtcDateTime;
        var expired = now.AddDays(-8);

        var oldProcessed = SealedFgaOutboxEntry.ForWrite("u:1", "r", "o:1");
        oldProcessed.ProcessedAtUtc = expired;
        var parked = SealedFgaOutboxEntry.ForDelete("u:x", "r", "o:x");
        parked.Attempts = 5;
        var parkedWitness = SealedFgaOutboxEntry.ForWrite("u:x", "r", "o:x");
        parkedWitness.ProcessedAtUtc = expired;
        var protectedFence = SealedFgaOutboxEntry.ForDeleteAllForObject("o:e", "o");
        protectedFence.ProcessedAtUtc = expired;
        var pendingBehindFence = SealedFgaOutboxEntry.ForWrite("u:2", "r", "o:e");
        pendingBehindFence.Attempts = 1;
        pendingBehindFence.NextAttemptUtc = now.AddMinutes(5);
        // Order matters: the pending row must be OLDER (lower Id) than the processed fence.
        ctx.Outbox.AddRange(oldProcessed, parked, parkedWitness, pendingBehindFence, protectedFence);
        await ctx.SaveChangesAsync();

        await SealedFgaOutboxRetentionSweeper.SweepAsync(ctx, TimeSpan.FromDays(7), 5, time);

        var rows = await ctx.Outbox.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        // Old unrelated processed row swept; parked row cleaned up (superseded), releasing its
        // witness for deletion; the fence is kept (it protects the older pending same-entity row).
        rows.Select(e => e.TupleUser ?? e.TargetId).ShouldBe(["u:x", "u:2", "o:e"]);
        rows[0].ProcessedAtUtc.ShouldNotBeNull(); // the cleaned-up parked row
        rows[0].LastError!.ShouldContain("Superseded");
    }
}
