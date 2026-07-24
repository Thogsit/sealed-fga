using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using SealedFga.Attributes;
using SealedFga.AuthModel;
using SealedFga.Fga;
using SealedFga.Fga.Outbox;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>
///     Unit tests for the <see cref="ISealedFgaTupleSource" /> path of
///     <see cref="SealedFgaSaveChangesProcessor" />: desired tuples are a pure function of the row,
///     and the processor enqueues exactly the diff of that function across each tracked change —
///     never a <c>DeleteAllForObject</c> purge fence.
/// </summary>
[Collection(GlobalCollection.Name)]
public class SaveChangesProcessorTupleSourceTests {
    private static List<SealedFgaOutboxEntry> PendingOutbox(DbContext ctx)
        => ctx.ChangeTracker.Entries<SealedFgaOutboxEntry>()
              .Where(e => e.State == EntityState.Added)
              .Select(e => e.Entity)
              .ToList();

    private static void Process(DbContext ctx) => new SealedFgaSaveChangesProcessor().ProcessSealedFgaChanges(ctx);

    private static TestGrantEntity NewGrant(TestGrantState state, bool canEdit = false)
        => new() {
            Id = TestGrantId.New(),
            State = state,
            UserId = new TestUserId("grant-user"),
            ObjectId = TestObjectId.New(),
            CanEdit = canEdit,
        };

    private static (string User, string Relation, string Object) Key(SealedFgaOutboxEntry e)
        => (e.TupleUser!, e.TupleRelation!, e.TupleObject!);

    private static (string User, string Relation, string Object) ShareGrantTuple(TestGrantEntity g)
        => (g.Id.AsOpenFgaIdTupleString(), "ShareGrant", g.ObjectId.AsOpenFgaIdTupleString());

    private static (string User, string Relation, string Object) CanViewTuple(TestGrantEntity g)
        => (g.UserId.AsOpenFgaIdTupleString(), "can_view", g.ObjectId.AsOpenFgaIdTupleString());

    private static (string User, string Relation, string Object) CanEditTuple(TestGrantEntity g)
        => (g.UserId.AsOpenFgaIdTupleString(), "can_edit", g.ObjectId.AsOpenFgaIdTupleString());

    [Fact]
    public void Added_inactive_grant_emits_nothing() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        ctx.Grants.Add(NewGrant(TestGrantState.Pending));
        Process(ctx);

        PendingOutbox(ctx).ShouldBeEmpty();
    }

    [Fact]
    public void Added_active_grant_writes_all_desired_tuples() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var grant = NewGrant(TestGrantState.Active, canEdit: true);
        ctx.Grants.Add(grant);
        Process(ctx);

        var rows = PendingOutbox(ctx);
        rows.ShouldAllBe(r => r.OperationType == SealedFgaOutboxOperationType.WriteTuple);
        rows.Select(Key).ShouldBe(
            [ShareGrantTuple(grant), CanViewTuple(grant), CanEditTuple(grant)],
            ignoreOrder: true
        );
    }

    [Fact]
    public void Activation_by_row_update_writes_the_appearing_tuples() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var grant = NewGrant(TestGrantState.Pending);
        ctx.Grants.Add(grant);
        ctx.SaveChanges(); // no interceptor wired → no outbox rows produced by seeding

        grant.State = TestGrantState.Active;
        Process(ctx);

        var rows = PendingOutbox(ctx);
        rows.ShouldAllBe(r => r.OperationType == SealedFgaOutboxOperationType.WriteTuple);
        rows.Select(Key).ShouldBe([ShareGrantTuple(grant), CanViewTuple(grant)], ignoreOrder: true);
    }

    [Fact]
    public void Permission_mutation_emits_only_the_difference() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var grant = NewGrant(TestGrantState.Active, canEdit: true);
        ctx.Grants.Add(grant);
        ctx.SaveChanges();

        grant.CanEdit = false;
        Process(ctx);

        var rows = PendingOutbox(ctx);
        rows.Count.ShouldBe(1);
        rows[0].OperationType.ShouldBe(SealedFgaOutboxOperationType.DeleteTuple);
        Key(rows[0]).ShouldBe(CanEditTuple(grant));
    }

    [Fact]
    public void Deactivation_by_row_update_deletes_all_tuples_row_survives() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var grant = NewGrant(TestGrantState.Active, canEdit: true);
        ctx.Grants.Add(grant);
        ctx.SaveChanges();

        grant.State = TestGrantState.Revoked;
        Process(ctx);

        var rows = PendingOutbox(ctx);
        rows.ShouldAllBe(r => r.OperationType == SealedFgaOutboxOperationType.DeleteTuple);
        rows.Select(Key).ShouldBe(
            [ShareGrantTuple(grant), CanViewTuple(grant), CanEditTuple(grant)],
            ignoreOrder: true
        );
    }

    [Fact]
    public void Unrelated_property_change_emits_nothing() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var grant = NewGrant(TestGrantState.Active);
        ctx.Grants.Add(grant);
        ctx.SaveChanges();

        grant.Note = "changed";
        Process(ctx);

        PendingOutbox(ctx).ShouldBeEmpty();
    }

    [Fact]
    public void Deleting_an_active_grant_deletes_desired_tuples_without_a_purge_fence() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var grant = NewGrant(TestGrantState.Active);
        ctx.Grants.Add(grant);
        ctx.SaveChanges();

        ctx.Grants.Remove(grant);
        Process(ctx);

        var rows = PendingOutbox(ctx);
        // The diff is exhaustive — no DeleteAllForObject fence, although the entity is an
        // ISealedFgaType (the fence could never reach the fan-out tuples anyway).
        rows.ShouldAllBe(r => r.OperationType == SealedFgaOutboxOperationType.DeleteTuple);
        rows.Select(Key).ShouldBe([ShareGrantTuple(grant), CanViewTuple(grant)], ignoreOrder: true);
    }

    [Fact]
    public void Deleting_an_inactive_grant_emits_nothing() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var grant = NewGrant(TestGrantState.Revoked);
        ctx.Grants.Add(grant);
        ctx.SaveChanges();

        ctx.Grants.Remove(grant);
        Process(ctx);

        PendingOutbox(ctx).ShouldBeEmpty();
    }

    [Fact]
    public void Modified_diff_uses_original_values_not_current_ones() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var grant = NewGrant(TestGrantState.Active);
        ctx.Grants.Add(grant);
        ctx.SaveChanges();

        // Retarget the active grant: the original object's tuples must be deleted and the new
        // object's written — which requires evaluating DesiredTuples over the ORIGINAL row values.
        var oldObject = grant.ObjectId;
        var newObject = TestObjectId.New();
        grant.ObjectId = newObject;
        Process(ctx);

        var rows = PendingOutbox(ctx);
        var deletes = rows.Where(r => r.OperationType == SealedFgaOutboxOperationType.DeleteTuple).ToList();
        var writes = rows.Where(r => r.OperationType == SealedFgaOutboxOperationType.WriteTuple).ToList();

        deletes.Select(Key).ShouldBe(
            [
                (grant.Id.AsOpenFgaIdTupleString(), "ShareGrant", oldObject.AsOpenFgaIdTupleString()),
                (grant.UserId.AsOpenFgaIdTupleString(), "can_view", oldObject.AsOpenFgaIdTupleString()),
            ],
            ignoreOrder: true
        );
        writes.Select(Key).ShouldBe([ShareGrantTuple(grant), CanViewTuple(grant)], ignoreOrder: true);
    }

    [Fact]
    public void Mixing_tuple_source_with_relation_attributes_throws() {
        using var ctx = MixedContext.CreateInMemory();
        ctx.Add(new MixedGrantEntity { Id = TestObjectId.New() });

        var ex = Should.Throw<InvalidOperationException>(() => Process(ctx));
        ex.Message.ShouldContain(nameof(ISealedFgaTupleSource));
        ex.Message.ShouldContain(nameof(MixedGrantEntity));
    }

    /// <summary>
    ///     Deliberately misconfigured: a tuple source that also declares a scalar relation attribute.
    ///     The Tests project references the Analyzers assembly as a plain library (not as an
    ///     analyzer), so SFGA004 does not fire at compile time here — which is exactly the situation
    ///     the processor's runtime backstop exists for.
    /// </summary>
    private sealed class MixedGrantEntity : ISealedFgaType<TestObjectId>, ISealedFgaTupleSource {
        public TestObjectId Id { get; set; }

        [SealedFgaRelation("OwnedBy")]
        public TestParentId? ParentId { get; set; }

        public IEnumerable<SealedFgaTupleOperation> DesiredTuples() => [];
    }

    private sealed class MixedContext(DbContextOptions<MixedContext> options) : DbContext(options) {
        public DbSet<MixedGrantEntity> Mixed => Set<MixedGrantEntity>();

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) {
            base.ConfigureConventions(configurationBuilder);
            configurationBuilder.Properties<TestObjectId>().HaveConversion<TestObjectId.EfCoreValueConverter>();
            configurationBuilder.Properties<TestParentId>().HaveConversion<TestParentId.EfCoreValueConverter>();
        }

        public static MixedContext CreateInMemory()
            => new(new DbContextOptionsBuilder<MixedContext>()
                  .UseInMemoryDatabase(Guid.NewGuid().ToString())
                  .Options);
    }
}
