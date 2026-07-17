using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using OpenFga.Sdk.Client.Model;
using SealedFga.AuthModel;
using SealedFga.Fga.Outbox;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.IntegrationTests;

/// <summary>
///     End-to-end tests for the typed public enqueue API: rows enqueued via
///     <see cref="SealedFgaOutboxEnqueueExtensions" /> ride the caller's transaction and are applied
///     to a real OpenFGA by the same drainer (same idempotency/ordering semantics) as interceptor rows.
/// </summary>
[Collection(OpenFgaCollection.Name)]
[Trait("Category", "Integration")]
public class OutboxEnqueueIntegrationTests(OpenFgaFixture fga) {
    private sealed class TestObjectRelation(string val) : SealedFgaRelation(val), ISealedFgaRelation<TestObjectId>;

    private sealed class TestParentRelation(string val) : SealedFgaRelation(val), ISealedFgaRelation<TestParentId>;

    private static readonly TestObjectRelation CanView = new("can_view");
    private static readonly TestParentRelation Member = new("Member");

    [Fact]
    public async Task Typed_write_then_delete_converges_in_openfga() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var user = new TestUserId("enq-e2e-user");
        var obj = TestObjectId.New();

        ctx.EnqueueFgaWrite(user, CanView, obj);
        await ctx.SaveChangesAsync();
        (await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, batchSize: 10, maxAttempts: 5)).ShouldBe(1);

        var read = await fga.Client.Read(new ClientReadRequest { Object = obj.AsOpenFgaIdTupleString() });
        read.Tuples.ShouldContain(t =>
            t.Key.User == user.AsOpenFgaIdTupleString() && t.Key.Relation == "can_view");

        ctx.EnqueueFgaDelete(user, CanView, obj);
        await ctx.SaveChangesAsync();
        (await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, batchSize: 10, maxAttempts: 5)).ShouldBe(1);

        read = await fga.Client.Read(new ClientReadRequest { Object = obj.AsOpenFgaIdTupleString() });
        read.Tuples.ShouldNotContain(t => t.Key.User == user.AsOpenFgaIdTupleString());
    }

    [Fact]
    public async Task Userset_subject_grants_access_through_group_membership() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var user = new TestUserId("enq-userset-user");
        var parent = TestParentId.New();
        var obj = TestObjectId.New();

        // user is Member of parent; parent#Member can_view obj — both via the typed API.
        ctx.EnqueueFgaWrite(user, Member, parent);
        ctx.EnqueueFgaWrite(SealedFgaUserset<TestParentId>.From(parent, Member), CanView, obj);
        await ctx.SaveChangesAsync();
        (await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, batchSize: 10, maxAttempts: 5)).ShouldBe(2);

        // The userset tuple must be stored verbatim...
        var read = await fga.Client.Read(new ClientReadRequest { Object = obj.AsOpenFgaIdTupleString() });
        read.Tuples.ShouldContain(t =>
            t.Key.User == $"{parent.AsOpenFgaIdTupleString()}#Member" && t.Key.Relation == "can_view");

        // ...and grant the member access transitively.
        (await fga.Service.CheckAsync(user, CanView, obj)).ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteAllForObject_enqueue_purges_both_object_and_subject_side_tuples() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var target = new TestUserId("enq-purge-target");
        var obj = TestObjectId.New();
        var parent = TestParentId.New();

        // The target appears on the user/subject side of two tuples (on an object and on a parent).
        ctx.EnqueueFgaWrite(target, CanView, obj);
        ctx.EnqueueFgaWrite(target, Member, parent);
        await ctx.SaveChangesAsync();
        (await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, batchSize: 10, maxAttempts: 5)).ShouldBe(2);

        (await fga.Client.Read(new ClientReadRequest { Object = obj.AsOpenFgaIdTupleString() })).Tuples.ShouldNotBeEmpty();

        // Enqueue the public fence and drain it: every tuple referencing the target must be gone.
        ctx.EnqueueFgaDeleteAllForObject(target);
        await ctx.SaveChangesAsync();
        (await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, batchSize: 10, maxAttempts: 5)).ShouldBe(1);

        (await fga.Client.Read(new ClientReadRequest { Object = obj.AsOpenFgaIdTupleString() })).Tuples.ShouldBeEmpty();
        (await fga.Client.Read(new ClientReadRequest { Object = parent.AsOpenFgaIdTupleString() })).Tuples.ShouldBeEmpty();
    }

    [Fact]
    public async Task Batch_enqueue_rides_the_transaction_and_drains_in_one_pass() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var user = new TestUserId("enq-batch-user");
        var objects = Enumerable.Range(0, 25).Select(_ => TestObjectId.New()).ToList();

        // A rolled-back transaction must leave no trace...
        await using (var tx = await ctx.Database.BeginTransactionAsync()) {
            ctx.EnqueueFga(objects.Select(o => SealedFgaTupleOperation.Of(user, CanView, o)), []);
            await ctx.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        ctx.ChangeTracker.Clear();
        ctx.Outbox.Count().ShouldBe(0);

        // ...while a committed one feeds the drainer, which applies the fan-out batched.
        await using (var tx = await ctx.Database.BeginTransactionAsync()) {
            ctx.EnqueueFga(objects.Select(o => SealedFgaTupleOperation.Of(user, CanView, o)), []);
            await ctx.SaveChangesAsync();
            await tx.CommitAsync();
        }

        (await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, batchSize: 100, maxAttempts: 5)).ShouldBe(25);
        ctx.Outbox.Count(e => e.ProcessedAtUtc != null).ShouldBe(25);

        var read = await fga.Client.Read(new ClientReadRequest { Object = objects[0].AsOpenFgaIdTupleString() });
        read.Tuples.ShouldContain(t => t.Key.User == user.AsOpenFgaIdTupleString());
    }
}
