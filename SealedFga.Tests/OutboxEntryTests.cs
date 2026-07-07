using SealedFga.Fga.Outbox;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>Unit tests for the <see cref="SealedFgaOutboxEntry" /> static factories.</summary>
public class OutboxEntryTests {
    [Fact]
    public void ForWrite_populates_tuple_columns() {
        var e = SealedFgaOutboxEntry.ForWrite("user:1", "can_view", "secret:2");

        e.OperationType.ShouldBe(SealedFgaOutboxOperationType.WriteTuple);
        e.TupleUser.ShouldBe("user:1");
        e.TupleRelation.ShouldBe("can_view");
        e.TupleObject.ShouldBe("secret:2");
        e.CreatedAtUtc.ShouldNotBe(default);
        e.ProcessedAtUtc.ShouldBeNull();
        e.Attempts.ShouldBe(0);
    }

    [Fact]
    public void ForDelete_populates_tuple_columns() {
        var e = SealedFgaOutboxEntry.ForDelete("user:1", "can_view", "secret:2");

        e.OperationType.ShouldBe(SealedFgaOutboxOperationType.DeleteTuple);
        e.TupleUser.ShouldBe("user:1");
        e.TupleRelation.ShouldBe("can_view");
        e.TupleObject.ShouldBe("secret:2");
    }

    [Fact]
    public void ForDeleteAllForObject_populates_command_columns() {
        var e = SealedFgaOutboxEntry.ForDeleteAllForObject("secret:2", "secret");

        e.OperationType.ShouldBe(SealedFgaOutboxOperationType.DeleteAllForObject);
        e.TargetId.ShouldBe("secret:2");
        e.TypeName.ShouldBe("secret");
        e.TupleUser.ShouldBeNull();
    }

    [Fact]
    public void ForModifyId_populates_old_and_new_target() {
        var e = SealedFgaOutboxEntry.ForModifyId("secret:1", "secret:2", "secret");

        e.OperationType.ShouldBe(SealedFgaOutboxOperationType.ModifyId);
        e.TargetId.ShouldBe("secret:1");
        e.NewTargetId.ShouldBe("secret:2");
        e.TypeName.ShouldBe("secret");
    }
}
