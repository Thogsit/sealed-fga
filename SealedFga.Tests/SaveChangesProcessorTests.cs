using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using SealedFga.Fga;
using SealedFga.Fga.Outbox;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>
///     Unit tests for <see cref="SealedFgaSaveChangesProcessor" /> — the translation of EF Core change
///     tracker state into outbox rows. The processor only <c>AddRange</c>s rows (it never saves), so we
///     inspect the pending (Added) <see cref="SealedFgaOutboxEntry" /> rows on the change tracker.
/// </summary>
[Collection(GlobalCollection.Name)]
public class SaveChangesProcessorTests {
    private static List<SealedFgaOutboxEntry> PendingOutbox(DbContext ctx)
        => ctx.ChangeTracker.Entries<SealedFgaOutboxEntry>()
              .Where(e => e.State == EntityState.Added)
              .Select(e => e.Entity)
              .OrderBy(e => e.OperationType)
              .ToList();

    private static void Process(DbContext ctx) => new SealedFgaSaveChangesProcessor().ProcessSealedFgaChanges(ctx);

    [Fact]
    public void Added_entity_with_object_target_relation_emits_one_write_tuple() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var parentId = new TestParentId(Guid.NewGuid());
        var obj = new TestObjectEntity { Id = TestObjectId.New(), ParentId = parentId, Payload = "x" };
        ctx.Objects.Add(obj);

        Process(ctx);

        var rows = PendingOutbox(ctx);
        rows.Count.ShouldBe(1);
        var row = rows[0];
        row.OperationType.ShouldBe(SealedFgaOutboxOperationType.WriteTuple);
        // TargetType.Object → FK (parent) is the tuple user, this entity is the object.
        row.TupleUser.ShouldBe(parentId.AsOpenFgaIdTupleString());
        row.TupleRelation.ShouldBe("OwnedBy");
        row.TupleObject.ShouldBe(obj.Id.AsOpenFgaIdTupleString());
    }

    [Fact]
    public void Added_entity_with_user_target_relation_swaps_user_and_object() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var parentId = new TestParentId(Guid.NewGuid());
        var user = new TestUserEntity { Id = new TestUserId("alice"), ParentId = parentId };
        ctx.Users.Add(user);

        Process(ctx);

        var rows = PendingOutbox(ctx);
        rows.Count.ShouldBe(1);
        var row = rows[0];
        row.OperationType.ShouldBe(SealedFgaOutboxOperationType.WriteTuple);
        // TargetType.User → this entity is the tuple user, the FK (parent) is the object.
        row.TupleUser.ShouldBe(user.Id.AsOpenFgaIdTupleString());
        row.TupleRelation.ShouldBe("Member");
        row.TupleObject.ShouldBe(parentId.AsOpenFgaIdTupleString());
    }

    [Fact]
    public void Added_entity_with_null_relation_emits_nothing() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var obj = new TestObjectEntity { Id = TestObjectId.New(), ParentId = null!, Payload = "x" };
        ctx.Objects.Add(obj);

        Process(ctx);

        PendingOutbox(ctx).ShouldBeEmpty();
    }

    [Fact]
    public void Modified_relation_value_emits_delete_old_then_write_new() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var oldParent = new TestParentId(Guid.NewGuid());
        var newParent = new TestParentId(Guid.NewGuid());
        var obj = new TestObjectEntity { Id = TestObjectId.New(), ParentId = oldParent, Payload = "x" };
        ctx.Objects.Add(obj);
        ctx.SaveChanges(); // no interceptor wired → no outbox rows produced by seeding

        obj.ParentId = newParent;
        Process(ctx);

        var rows = PendingOutbox(ctx);
        rows.Count.ShouldBe(2);

        var delete = rows.Single(r => r.OperationType == SealedFgaOutboxOperationType.DeleteTuple);
        delete.TupleUser.ShouldBe(oldParent.AsOpenFgaIdTupleString());
        delete.TupleRelation.ShouldBe("OwnedBy");
        delete.TupleObject.ShouldBe(obj.Id.AsOpenFgaIdTupleString());

        var write = rows.Single(r => r.OperationType == SealedFgaOutboxOperationType.WriteTuple);
        write.TupleUser.ShouldBe(newParent.AsOpenFgaIdTupleString());
        write.TupleObject.ShouldBe(obj.Id.AsOpenFgaIdTupleString());
    }

    [Fact]
    public void Modified_entity_with_unchanged_relation_emits_nothing() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var parent = new TestParentId(Guid.NewGuid());
        var obj = new TestObjectEntity { Id = TestObjectId.New(), ParentId = parent, Payload = "before" };
        ctx.Objects.Add(obj);
        ctx.SaveChanges();

        // Change a non-relation property only; relation value is unchanged (compared by value).
        obj.Payload = "after";
        Process(ctx);

        PendingOutbox(ctx).ShouldBeEmpty();
    }

    [Fact]
    public void Modified_relation_reassigned_to_equal_value_emits_nothing() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var pid = Guid.NewGuid();
        var obj = new TestObjectEntity { Id = TestObjectId.New(), ParentId = new TestParentId(pid), Payload = "x" };
        ctx.Objects.Add(obj);
        ctx.SaveChanges();

        // A brand-new instance with the SAME underlying value: value equality must suppress churn.
        obj.ParentId = new TestParentId(pid);
        Process(ctx);

        PendingOutbox(ctx).ShouldBeEmpty();
    }

    [Fact]
    public void Deleted_entity_emits_single_delete_all_for_object() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var obj = new TestObjectEntity { Id = TestObjectId.New(), ParentId = new TestParentId(Guid.NewGuid()), Payload = "x" };
        ctx.Objects.Add(obj);
        ctx.SaveChanges();

        ctx.Objects.Remove(obj);
        Process(ctx);

        var rows = PendingOutbox(ctx);
        rows.Count.ShouldBe(1);
        var row = rows[0];
        row.OperationType.ShouldBe(SealedFgaOutboxOperationType.DeleteAllForObject);
        row.TargetId.ShouldBe(obj.Id.AsOpenFgaIdTupleString());
        row.TypeName.ShouldBe(TestObjectId.OpenFgaTypeName);
    }

    [Fact]
    public void Null_context_is_a_noop() {
        // Should not throw.
        new SealedFgaSaveChangesProcessor().ProcessSealedFgaChanges(null);
    }

    [Fact]
    public void Entity_not_implementing_interface_is_ignored() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        // Parent entity implements ISealedFgaType but has no [SealedFgaRelation] properties.
        ctx.Parents.Add(new TestParentEntity { Id = TestParentId.New(), Name = "n" });
        Process(ctx);

        PendingOutbox(ctx).ShouldBeEmpty();
    }
}
