using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using SealedFga.AuthModel;
using SealedFga.Fga.Outbox;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>
///     Unit tests for the typed public enqueue API — <see cref="SealedFgaOutboxEnqueueExtensions" />.
///     Enqueue only adds rows to the change tracker; persistence rides the caller's own
///     SaveChanges/transaction, so commit and rollback semantics are tested through SQLite transactions.
/// </summary>
[Collection(GlobalCollection.Name)]
public class OutboxEnqueueExtensionsTests {
    /// <summary>A relation constant bound to <see cref="TestObjectId" />, as the generator would emit.</summary>
    private sealed class TestObjectRelation(string val) : SealedFgaRelation(val), ISealedFgaRelation<TestObjectId>;

    /// <summary>A relation constant bound to <see cref="TestParentId" /> (for the userset subject).</summary>
    private sealed class TestParentRelation(string val) : SealedFgaRelation(val), ISealedFgaRelation<TestParentId>;

    private static readonly TestObjectRelation CanView = new("can_view");
    private static readonly TestParentRelation Member = new("Member");

    private static List<SealedFgaOutboxEntry> PendingOutbox(DbContext ctx)
        => ctx.ChangeTracker.Entries<SealedFgaOutboxEntry>()
              .Where(e => e.State == EntityState.Added)
              .Select(e => e.Entity)
              .ToList();

    [Fact]
    public void EnqueueFgaWrite_adds_one_pending_write_row() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var user = new TestUserId("alice");
        var obj = TestObjectId.New();
        ctx.EnqueueFgaWrite(user, CanView, obj);

        var rows = PendingOutbox(ctx);
        rows.Count.ShouldBe(1);
        var row = rows[0];
        row.OperationType.ShouldBe(SealedFgaOutboxOperationType.WriteTuple);
        row.TupleUser.ShouldBe(user.AsOpenFgaIdTupleString());
        row.TupleRelation.ShouldBe("can_view");
        row.TupleObject.ShouldBe(obj.AsOpenFgaIdTupleString());
    }

    [Fact]
    public void EnqueueFgaDelete_adds_one_pending_delete_row() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var user = new TestUserId("bob");
        var obj = TestObjectId.New();
        ctx.EnqueueFgaDelete(user, CanView, obj);

        var rows = PendingOutbox(ctx);
        rows.Count.ShouldBe(1);
        rows[0].OperationType.ShouldBe(SealedFgaOutboxOperationType.DeleteTuple);
        rows[0].TupleUser.ShouldBe(user.AsOpenFgaIdTupleString());
        rows[0].TupleObject.ShouldBe(obj.AsOpenFgaIdTupleString());
    }

    [Fact]
    public void Userset_subject_is_stringified_as_type_id_relation() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var parentId = TestParentId.New();
        var subject = SealedFgaUserset<TestParentId>.From(parentId, Member);
        var obj = TestObjectId.New();
        ctx.EnqueueFgaWrite(subject, CanView, obj);

        var row = PendingOutbox(ctx).Single();
        row.TupleUser.ShouldBe($"{parentId.AsOpenFgaIdTupleString()}#Member");
    }

    [Fact]
    public void EnqueueFga_batch_adds_writes_and_deletes_in_one_call() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var user = new TestUserId("carol");
        var writes = Enumerable.Range(0, 3)
                               .Select(_ => SealedFgaTupleOperation.Of(user, CanView, TestObjectId.New()))
                               .ToList();
        var deletes = new[] { SealedFgaTupleOperation.Of(user, CanView, TestObjectId.New()) };

        ctx.EnqueueFga(writes, deletes);

        var rows = PendingOutbox(ctx);
        rows.Count(r => r.OperationType == SealedFgaOutboxOperationType.WriteTuple).ShouldBe(3);
        rows.Count(r => r.OperationType == SealedFgaOutboxOperationType.DeleteTuple).ShouldBe(1);
        rows.ShouldAllBe(r => r.TupleUser == user.AsOpenFgaIdTupleString());
    }

    [Fact]
    public void EnqueueFgaDeleteAllForObject_adds_a_fence_row_matching_the_interceptor_shape() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var obj = TestObjectId.New();
        ctx.EnqueueFgaDeleteAllForObject(obj);

        var row = PendingOutbox(ctx).Single();
        row.OperationType.ShouldBe(SealedFgaOutboxOperationType.DeleteAllForObject);
        row.TargetId.ShouldBe(obj.AsOpenFgaIdTupleString());
        row.TypeName.ShouldBe(TestObjectId.OpenFgaTypeName);
        // Fences carry no tuple columns.
        row.TupleUser.ShouldBeNull();
        row.TupleRelation.ShouldBeNull();
        row.TupleObject.ShouldBeNull();
    }

    [Fact]
    public void EnqueueFgaDeleteAllForObject_rejects_a_default_id() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        Should.Throw<ArgumentException>(() => ctx.EnqueueFgaDeleteAllForObject(default(TestObjectId)));
        PendingOutbox(ctx).ShouldBeEmpty();
    }

    [Fact]
    public void EnqueueFgaDeleteAllForObject_on_context_without_outbox_throws() {
        using var ctx = new NoOutboxDbContext(
            new DbContextOptionsBuilder<NoOutboxDbContext>()
               .UseInMemoryDatabase(Guid.NewGuid().ToString())
               .Options
        );

        Should.Throw<InvalidOperationException>(() => ctx.EnqueueFgaDeleteAllForObject(TestObjectId.New()))
              .Message.ShouldContain("AddSealedFga");
    }

    [Fact]
    public void EnqueueFga_with_empty_batches_adds_nothing() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        ctx.EnqueueFga([], []);

        PendingOutbox(ctx).ShouldBeEmpty();
    }

    [Fact]
    public void Committed_transaction_persists_enqueued_rows() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        using (var tx = ctx.Database.BeginTransaction()) {
            ctx.EnqueueFgaWrite(new TestUserId("dave"), CanView, TestObjectId.New());
            ctx.SaveChanges();
            tx.Commit();
        }

        ctx.Outbox.Count().ShouldBe(1);
    }

    [Fact]
    public void Rolled_back_transaction_discards_enqueued_rows() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        using (var tx = ctx.Database.BeginTransaction()) {
            ctx.EnqueueFgaWrite(new TestUserId("erin"), CanView, TestObjectId.New());
            ctx.SaveChanges();
            tx.Rollback();
        }

        ctx.ChangeTracker.Clear();
        ctx.Outbox.Count().ShouldBe(0);
    }

    [Fact]
    public void Context_without_outbox_throws_pointing_at_setup() {
        using var ctx = new NoOutboxDbContext(
            new DbContextOptionsBuilder<NoOutboxDbContext>()
               .UseInMemoryDatabase(Guid.NewGuid().ToString())
               .Options
        );

        var ex = Should.Throw<InvalidOperationException>(
            () => ctx.EnqueueFgaWrite(new TestUserId("frank"), CanView, TestObjectId.New())
        );
        ex.Message.ShouldContain(nameof(NoOutboxDbContext));
        ex.Message.ShouldContain("AddSealedFga");
    }

    [Fact]
    public void Default_tuple_operation_is_rejected() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        Should.Throw<ArgumentException>(() => ctx.EnqueueFga([default], []));
        PendingOutbox(ctx).ShouldBeEmpty();
    }

    [Fact]
    public void Null_arguments_are_rejected() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        Should.Throw<ArgumentNullException>(() => ctx.EnqueueFgaWrite(null!, CanView, TestObjectId.New()));
        Should.Throw<ArgumentNullException>(() => ctx.EnqueueFgaWrite(new TestUserId("x"), null!, TestObjectId.New()));
        Should.Throw<ArgumentNullException>(() => ctx.EnqueueFga(null!, []));
        Should.Throw<ArgumentNullException>(() => ctx.EnqueueFga([], null!));
    }
}

/// <summary>A context that never configured the SealedFGA outbox — enqueue must fail loud on it.</summary>
public class NoOutboxDbContext(DbContextOptions<NoOutboxDbContext> options) : DbContext(options) {
    public DbSet<PlainEntity> Plain => Set<PlainEntity>();

    public class PlainEntity {
        public Guid Id { get; set; }
    }
}
