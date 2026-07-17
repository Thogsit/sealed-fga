using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using SealedFga.Attributes;
using SealedFga.Fga;
using SealedFga.Fga.Outbox;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>
///     Unit tests for the join-entity path of <see cref="SealedFgaSaveChangesProcessor" />:
///     a class-level <see cref="SealedFgaJoinRelationAttribute" /> emits tuples linking the entity's
///     two FK ends — the join row's own PK appears on neither side, and the entity does not implement
///     <c>ISealedFgaType&lt;TId&gt;</c>.
/// </summary>
[Collection(GlobalCollection.Name)]
public class SaveChangesProcessorJoinTests {
    private static List<SealedFgaOutboxEntry> PendingOutbox(DbContext ctx)
        => ctx.ChangeTracker.Entries<SealedFgaOutboxEntry>()
              .Where(e => e.State == EntityState.Added)
              .Select(e => e.Entity)
              .OrderBy(e => e.OperationType)
              .ToList();

    private static void Process(DbContext ctx) => new SealedFgaSaveChangesProcessor().ProcessSealedFgaChanges(ctx);

    [Fact]
    public void Added_join_entity_emits_write_tuple_linking_both_fk_ends() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var userId = new TestUserId("alice");
        var objectId = TestObjectId.New();
        ctx.Joins.Add(new TestJoinEntity { Id = Guid.NewGuid(), UserId = userId, ObjectId = objectId });

        Process(ctx);

        var rows = PendingOutbox(ctx);
        rows.Count.ShouldBe(1);
        var row = rows[0];
        row.OperationType.ShouldBe(SealedFgaOutboxOperationType.WriteTuple);
        row.TupleUser.ShouldBe(userId.AsOpenFgaIdTupleString());
        row.TupleRelation.ShouldBe("can_view");
        row.TupleObject.ShouldBe(objectId.AsOpenFgaIdTupleString());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Added_join_entity_with_incomplete_fk_pair_emits_nothing(bool nullUser, bool nullObject) {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        ctx.Joins.Add(new TestJoinEntity {
            Id = Guid.NewGuid(),
            UserId = nullUser ? null : new TestUserId("alice"),
            ObjectId = nullObject ? null : TestObjectId.New(),
        });

        Process(ctx);

        PendingOutbox(ctx).ShouldBeEmpty();
    }

    [Fact]
    public void Modified_join_fk_emits_delete_of_old_pair_then_write_of_new_pair() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var userId = new TestUserId("alice");
        var oldObject = TestObjectId.New();
        var newObject = TestObjectId.New();
        var join = new TestJoinEntity { Id = Guid.NewGuid(), UserId = userId, ObjectId = oldObject };
        ctx.Joins.Add(join);
        ctx.SaveChanges(); // no interceptor wired → no outbox rows produced by seeding

        join.ObjectId = newObject;
        Process(ctx);

        var rows = PendingOutbox(ctx);
        rows.Count.ShouldBe(2);

        var delete = rows.Single(r => r.OperationType == SealedFgaOutboxOperationType.DeleteTuple);
        delete.TupleUser.ShouldBe(userId.AsOpenFgaIdTupleString());
        delete.TupleRelation.ShouldBe("can_view");
        delete.TupleObject.ShouldBe(oldObject.AsOpenFgaIdTupleString());

        var write = rows.Single(r => r.OperationType == SealedFgaOutboxOperationType.WriteTuple);
        write.TupleUser.ShouldBe(userId.AsOpenFgaIdTupleString());
        write.TupleObject.ShouldBe(newObject.AsOpenFgaIdTupleString());
    }

    [Fact]
    public void Modified_join_fk_from_null_emits_only_the_write() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var join = new TestJoinEntity { Id = Guid.NewGuid(), UserId = new TestUserId("alice"), ObjectId = null };
        ctx.Joins.Add(join);
        ctx.SaveChanges();

        join.ObjectId = TestObjectId.New();
        Process(ctx);

        var rows = PendingOutbox(ctx);
        rows.Count.ShouldBe(1);
        rows[0].OperationType.ShouldBe(SealedFgaOutboxOperationType.WriteTuple);
    }

    [Fact]
    public void Modified_join_fk_reassigned_to_equal_value_emits_nothing() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var objectGuid = Guid.NewGuid();
        var join = new TestJoinEntity {
            Id = Guid.NewGuid(),
            UserId = new TestUserId("alice"),
            ObjectId = new TestObjectId(objectGuid),
        };
        ctx.Joins.Add(join);
        ctx.SaveChanges();

        // A brand-new instance with the SAME underlying value: value equality must suppress churn.
        join.ObjectId = new TestObjectId(objectGuid);
        Process(ctx);

        PendingOutbox(ctx).ShouldBeEmpty();
    }

    [Fact]
    public void Deleted_join_entity_emits_single_targeted_tuple_delete() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var userId = new TestUserId("alice");
        var objectId = TestObjectId.New();
        var join = new TestJoinEntity { Id = Guid.NewGuid(), UserId = userId, ObjectId = objectId };
        ctx.Joins.Add(join);
        ctx.SaveChanges();

        ctx.Joins.Remove(join);
        Process(ctx);

        var rows = PendingOutbox(ctx);
        rows.Count.ShouldBe(1);
        var row = rows[0];
        // Targeted delete of exactly the join tuple — NOT a DeleteAllForObject fence: the join row's
        // own PK appears in no tuple, and fences are ordering barriers backed by store scans.
        row.OperationType.ShouldBe(SealedFgaOutboxOperationType.DeleteTuple);
        row.TupleUser.ShouldBe(userId.AsOpenFgaIdTupleString());
        row.TupleRelation.ShouldBe("can_view");
        row.TupleObject.ShouldBe(objectId.AsOpenFgaIdTupleString());
    }

    [Fact]
    public void Deleted_join_entity_with_incomplete_fk_pair_emits_nothing() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var join = new TestJoinEntity { Id = Guid.NewGuid(), UserId = new TestUserId("alice"), ObjectId = null };
        ctx.Joins.Add(join);
        ctx.SaveChanges();

        ctx.Joins.Remove(join);
        Process(ctx);

        PendingOutbox(ctx).ShouldBeEmpty();
    }

    [Fact]
    public void Join_annotation_naming_missing_property_throws_naming_entity_and_property() {
        using var ctx = MisconfigDbContext.Create();
        ctx.Add(new JoinWithMissingProperty { Id = Guid.NewGuid(), ObjectId = TestObjectId.New() });

        var ex = Should.Throw<InvalidOperationException>(() => Process(ctx));
        ex.Message.ShouldContain(nameof(JoinWithMissingProperty));
        ex.Message.ShouldContain("DoesNotExist");
    }

    [Fact]
    public void Join_annotation_on_non_id_property_throws_naming_entity_and_property() {
        using var ctx = MisconfigDbContext.Create();
        ctx.Add(new JoinWithNonIdProperty { Id = Guid.NewGuid(), Payload = "x", ObjectId = TestObjectId.New() });

        var ex = Should.Throw<InvalidOperationException>(() => Process(ctx));
        ex.Message.ShouldContain(nameof(JoinWithNonIdProperty));
        ex.Message.ShouldContain(nameof(JoinWithNonIdProperty.Payload));
    }

    [Fact]
    public void Scalar_relation_on_entity_without_fga_type_throws() {
        using var ctx = MisconfigDbContext.Create();
        ctx.Add(new ScalarWithoutFgaType { Id = Guid.NewGuid(), ParentId = TestParentId.New() });

        var ex = Should.Throw<InvalidOperationException>(() => Process(ctx));
        ex.Message.ShouldContain(nameof(ScalarWithoutFgaType));
        ex.Message.ShouldContain("ISealedFgaType");
    }
}

// --- Misconfigured entities: each must fail loud at the first SaveChanges touching the type. ---

/// <summary>Join annotation referencing a property that does not exist.</summary>
[SealedFgaJoinRelation("can_view", "DoesNotExist", nameof(ObjectId))]
public class JoinWithMissingProperty {
    public Guid Id { get; set; }
    public TestObjectId? ObjectId { get; set; }
}

/// <summary>Join annotation referencing a property that is not a strongly-typed SealedFGA ID.</summary>
[SealedFgaJoinRelation("can_view", nameof(Payload), nameof(ObjectId))]
public class JoinWithNonIdProperty {
    public Guid Id { get; set; }
    public string Payload { get; set; } = "";
    public TestObjectId? ObjectId { get; set; }
}

/// <summary>[SealedFgaRelation] requires the entity's own Id — invalid without ISealedFgaType.</summary>
public class ScalarWithoutFgaType {
    public Guid Id { get; set; }

    [SealedFgaRelation("OwnedBy")]
    public TestParentId? ParentId { get; set; }
}

/// <summary>An InMemory context holding only the deliberately misconfigured entities.</summary>
public class MisconfigDbContext(DbContextOptions<MisconfigDbContext> options) : DbContext(options) {
    public DbSet<JoinWithMissingProperty> MissingProperty => Set<JoinWithMissingProperty>();
    public DbSet<JoinWithNonIdProperty> NonIdProperty => Set<JoinWithNonIdProperty>();
    public DbSet<ScalarWithoutFgaType> ScalarWithoutType => Set<ScalarWithoutFgaType>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<TestObjectId>().HaveConversion<TestObjectId.EfCoreValueConverter>();
        configurationBuilder.Properties<TestParentId>().HaveConversion<TestParentId.EfCoreValueConverter>();
    }

    public static MisconfigDbContext Create()
        => new(new DbContextOptionsBuilder<MisconfigDbContext>()
              .UseInMemoryDatabase(Guid.NewGuid().ToString())
              .Options);
}
