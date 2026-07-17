using System;
using System.Collections.Generic;
using System.Linq;
using SealedFga.Fga.Outbox;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>
///     Pure tests of the drain planner's ordering rules: newest-wins supersession, fence
///     supersession, per-entity fences with asymmetric parked semantics, deferral, segmentation,
///     and per-key coalescing. No database or OpenFGA involved.
/// </summary>
public class OutboxDrainPlannerTests {
    private static readonly DateTime Now = new(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(300);
    private const int MaxAttempts = 5;

    private const string UserX = "testuser:alice";
    private const string RelX = "can_view";
    private const string ObjX = "testobject:11111111-1111-1111-1111-111111111111";
    private const string EntityE = "testobject:22222222-2222-2222-2222-222222222222";

    private static SealedFgaOutboxEntry WriteRow(
        long id, string user = UserX, string relation = RelX, string obj = ObjX,
        int attempts = 0, DateTime? nextAttempt = null, DateTime? processed = null
    ) => new() {
        Id = id, OperationType = SealedFgaOutboxOperationType.WriteTuple,
        TupleUser = user, TupleRelation = relation, TupleObject = obj,
        Attempts = attempts, NextAttemptUtc = nextAttempt, ProcessedAtUtc = processed,
    };

    private static SealedFgaOutboxEntry DeleteRow(
        long id, string user = UserX, string relation = RelX, string obj = ObjX,
        int attempts = 0, DateTime? nextAttempt = null, DateTime? processed = null
    ) => new() {
        Id = id, OperationType = SealedFgaOutboxOperationType.DeleteTuple,
        TupleUser = user, TupleRelation = relation, TupleObject = obj,
        Attempts = attempts, NextAttemptUtc = nextAttempt, ProcessedAtUtc = processed,
    };

    private static SealedFgaOutboxEntry FenceRow(
        long id, string targetId, int attempts = 0, DateTime? nextAttempt = null, DateTime? processed = null
    ) => new() {
        Id = id, OperationType = SealedFgaOutboxOperationType.DeleteAllForObject,
        TargetId = targetId, TypeName = targetId.Split(':')[0],
        Attempts = attempts, NextAttemptUtc = nextAttempt, ProcessedAtUtc = processed,
    };

    private static DrainPlan Plan(
        IReadOnlyList<SealedFgaOutboxEntry> claimed,
        IReadOnlyList<SealedFgaOutboxEntry>? blockers = null,
        IReadOnlyList<SealedFgaOutboxEntry>? witnesses = null,
        IReadOnlyList<SealedFgaOutboxEntry>? processedFences = null
    ) => SealedFgaOutboxDrainPlanner.Plan(
        claimed, blockers ?? [], witnesses ?? [], processedFences ?? [], Now, MaxAttempts, MaxBackoff);

    [Fact]
    public void F2_scenario_parked_retry_is_superseded_by_newer_processed_same_key_row() {
        // Del(X) failed and retries after the X↔Y flip-flop already applied a newer Write(X).
        var retry = DeleteRow(1, attempts: 2);
        var plan = Plan([retry], witnesses: [WriteRow(4, processed: Now.AddMinutes(-1))]);

        plan.Superseded.ShouldHaveSingleItem().ShouldBe((retry, 4L));
        plan.Segments.ShouldBeEmpty();
        plan.Deferred.ShouldBeEmpty();
    }

    [Fact]
    public void Witness_with_lower_id_does_not_supersede() {
        var row = WriteRow(10);
        var plan = Plan([row], witnesses: [DeleteRow(3, processed: Now.AddMinutes(-1))]);

        plan.Superseded.ShouldBeEmpty();
        plan.Segments.ShouldHaveSingleItem().ApplyRows.ShouldBe([row]);
    }

    [Fact]
    public void Coalescing_keeps_only_newest_op_per_key() {
        var w1 = WriteRow(1);
        var d2 = DeleteRow(2);
        var w3 = WriteRow(3);
        var plan = Plan([w1, d2, w3]);

        plan.Segments.ShouldHaveSingleItem().ApplyRows.ShouldBe([w3]);
        plan.Superseded.Select(s => (s.Row.Id, s.SupersededById))
            .ShouldBe([(1L, 3L), (2L, 3L)], ignoreOrder: true);
    }

    [Fact]
    public void Interleaved_ineligible_middle_row_converges_via_skip_rule() {
        // Claim {1,3}; row 2 (same key, backed off) is skipped over by coalescing...
        var w1 = WriteRow(1);
        var w3 = WriteRow(3);
        var plan1 = Plan([w1, w3]);
        plan1.Segments.ShouldHaveSingleItem().ApplyRows.ShouldBe([w3]);

        // ...and caught by the newest-wins skip rule at its own retry.
        var d2 = DeleteRow(2, attempts: 1);
        var plan2 = Plan([d2], witnesses: [WriteRow(3, processed: Now)]);
        plan2.Superseded.ShouldHaveSingleItem().ShouldBe((d2, 3L));
    }

    [Fact]
    public void Live_fence_segments_the_batch_and_prevents_cross_fence_coalescing() {
        var w1 = WriteRow(1);
        var fence = FenceRow(2, EntityE); // unrelated entity
        var w3 = WriteRow(3);
        var plan = Plan([w1, fence, w3]);

        plan.Segments.Count.ShouldBe(2);
        plan.Segments[0].ApplyRows.ShouldBe([w1]);
        plan.Segments[0].Fence.ShouldBe(fence);
        plan.Segments[1].ApplyRows.ShouldBe([w3]);
        plan.Segments[1].Fence.ShouldBeNull();
        plan.Superseded.ShouldBeEmpty(); // same key, but no coalescing across the segment split
    }

    [Fact]
    public void In_batch_fence_orders_same_entity_rows_via_segments() {
        var w1 = WriteRow(1, obj: EntityE);
        var fence = FenceRow(2, EntityE);
        var w3 = WriteRow(3, obj: EntityE);
        var plan = Plan([w1, fence, w3]);

        plan.Segments.Count.ShouldBe(2);
        plan.Segments[0].ApplyRows.ShouldBe([w1]);
        plan.Segments[0].Fence.ShouldBe(fence);
        plan.Segments[1].ApplyRows.ShouldBe([w3]);
        plan.Deferred.ShouldBeEmpty();
    }

    [Fact]
    public void Backed_off_fence_defers_newer_same_entity_rows_to_its_retry_time() {
        var fenceRetryAt = Now.AddSeconds(120);
        var blocker = FenceRow(1, EntityE, attempts: 2, nextAttempt: fenceRetryAt);
        var row = WriteRow(3, obj: EntityE);
        var unrelated = WriteRow(4, obj: ObjX);
        var plan = Plan([row, unrelated], blockers: [blocker]);

        var deferral = plan.Deferred.ShouldHaveSingleItem();
        deferral.Row.ShouldBe(row);
        deferral.BlockedById.ShouldBe(1L);
        deferral.BlockerParked.ShouldBeFalse();
        deferral.NextAttemptUtc.ShouldBe(fenceRetryAt);
        plan.Segments.ShouldHaveSingleItem().ApplyRows.ShouldBe([unrelated]);
    }

    [Fact]
    public void Parked_fence_blocks_newer_same_entity_rows_with_max_backoff() {
        var blocker = FenceRow(1, EntityE, attempts: MaxAttempts);
        var row = WriteRow(3, obj: EntityE);
        var plan = Plan([row], blockers: [blocker]);

        var deferral = plan.Deferred.ShouldHaveSingleItem();
        deferral.BlockerParked.ShouldBeTrue();
        deferral.NextAttemptUtc.ShouldBe(Now.Add(MaxBackoff));
    }

    [Fact]
    public void Parked_tuple_row_does_not_block_fence_but_backed_off_one_does() {
        // Asymmetric rule, both directions.
        var parkedTuple = WriteRow(1, obj: EntityE, attempts: MaxAttempts);
        var fence = FenceRow(5, EntityE);
        Plan([fence], blockers: [parkedTuple])
            .Segments.ShouldHaveSingleItem().Fence.ShouldBe(fence);

        var backedOffTuple = WriteRow(1, obj: EntityE, attempts: 1, nextAttempt: Now.AddSeconds(60));
        var plan = Plan([fence], blockers: [backedOffTuple]);
        plan.Segments.ShouldBeEmpty();
        var deferral = plan.Deferred.ShouldHaveSingleItem();
        deferral.Row.ShouldBe(fence);
        deferral.NextAttemptUtc.ShouldBe(Now.AddSeconds(60));
    }

    [Fact]
    public void Deferred_row_cascades_to_later_same_entity_fence_and_rows() {
        var blocker = FenceRow(1, EntityE, attempts: 1, nextAttempt: Now.AddSeconds(90));
        var row2 = WriteRow(2, obj: EntityE);
        var fence3 = FenceRow(3, EntityE);
        var row4 = WriteRow(4, obj: EntityE);
        var plan = Plan([row2, fence3, row4], blockers: [blocker]);

        plan.Deferred.Count.ShouldBe(3);
        plan.Deferred.Select(d => d.Row).ShouldBe([row2, fence3, row4]);
        plan.Deferred.ShouldAllBe(d => d.NextAttemptUtc > Now);
        plan.Segments.ShouldBeEmpty();
    }

    [Fact]
    public void Userset_subject_matches_fence_target_by_prefix() {
        var fence = FenceRow(1, "testuser:bob", attempts: 1, nextAttempt: Now.AddSeconds(30));
        var usersetRow = WriteRow(2, user: "testuser:bob#member", obj: EntityE);
        var lookalikeRow = WriteRow(3, user: "testuser:bob2", obj: ObjX);
        var plan = Plan([usersetRow, lookalikeRow], blockers: [fence]);

        plan.Deferred.ShouldHaveSingleItem().Row.ShouldBe(usersetRow);
        plan.Segments.ShouldHaveSingleItem().ApplyRows.ShouldBe([lookalikeRow]);
    }

    [Fact]
    public void Processed_fence_with_higher_id_supersedes_older_tuple_row() {
        // Enqueue commit skew: the row became visible after the entity purge already applied.
        // Applying it would resurrect tuples of a deleted entity.
        var row = WriteRow(2, obj: EntityE);
        var plan = Plan([row], processedFences: [FenceRow(5, EntityE, processed: Now.AddMinutes(-1))]);

        plan.Superseded.ShouldHaveSingleItem().ShouldBe((row, 5L));
    }

    [Fact]
    public void Processed_fence_with_lower_id_does_not_supersede() {
        var row = WriteRow(7, obj: EntityE);
        var plan = Plan([row], processedFences: [FenceRow(5, EntityE, processed: Now.AddMinutes(-1))]);

        plan.Segments.ShouldHaveSingleItem().ApplyRows.ShouldBe([row]);
    }

    [Fact]
    public void Fully_deferred_batch_produces_no_live_work_and_future_retry_times() {
        var blocker = FenceRow(1, EntityE, attempts: MaxAttempts);
        var rows = Enumerable.Range(2, 5).Select(i => WriteRow(i, obj: EntityE)).ToList();
        var plan = Plan(rows, blockers: [blocker]);

        plan.Segments.ShouldBeEmpty();
        plan.Deferred.Count.ShouldBe(5);
        plan.Deferred.ShouldAllBe(d => d.NextAttemptUtc > Now); // termination: rows become ineligible
    }
}

/// <summary>Edge cases of the entity/fence matching helpers.</summary>
public class OutboxEntryMatchingTests {
    [Theory]
    [InlineData("testobject:1", null, "testobject:1", true)] // object side
    [InlineData("testuser:bob", "testuser:bob", "testobject:1", true)] // user side
    [InlineData("testuser:bob", "testuser:bob#member", "testobject:1", true)] // userset prefix
    [InlineData("testuser:bob", "testuser:bob2", "testobject:1", false)] // no false prefix hit
    [InlineData("testobject:1", "testuser:bob", "testobject:12", false)] // no substring match
    public void FenceMatchesTupleRow_matches_object_user_and_userset(
        string target, string? user, string obj, bool expected
    ) {
        var row = new SealedFgaOutboxEntry {
            OperationType = SealedFgaOutboxOperationType.WriteTuple,
            TupleUser = user, TupleRelation = "r", TupleObject = obj,
        };
        OutboxEntryMatching.FenceMatchesTupleRow(target, row).ShouldBe(expected);
    }

    [Fact]
    public void EntitiesOf_strips_userset_suffix_and_dedupes() {
        var row = new SealedFgaOutboxEntry {
            OperationType = SealedFgaOutboxOperationType.WriteTuple,
            TupleUser = "testuser:bob#member", TupleRelation = "r", TupleObject = "testobject:1",
        };
        OutboxEntryMatching.EntitiesOf(row).ShouldBe(["testobject:1", "testuser:bob"]);

        var selfRef = new SealedFgaOutboxEntry {
            OperationType = SealedFgaOutboxOperationType.WriteTuple,
            TupleUser = "testobject:1", TupleRelation = "r", TupleObject = "testobject:1",
        };
        OutboxEntryMatching.EntitiesOf(selfRef).ShouldBe(["testobject:1"]);
    }
}
