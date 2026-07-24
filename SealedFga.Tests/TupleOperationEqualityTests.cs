using System;
using System.Collections.Generic;
using SealedFga.Fga;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>
///     Value-equality contract of <see cref="SealedFgaTupleOperation" />: two operations are equal
///     iff their <c>(user, relation, object)</c> triplets are — the set semantics both the
///     tuple-source diff and consumer tests over <c>DesiredTuples()</c> rely on.
/// </summary>
public class TupleOperationEqualityTests {
    private static readonly TestUserId User = new("alice");
    private static readonly TestObjectId Object = TestObjectId.Parse("199e1e9e-52e4-45cb-93fc-bb0d43a1f11b");

    [Fact]
    public void Same_triplet_from_distinct_id_instances_is_equal() {
        var left = SealedFgaTupleOperation.Of(new TestUserId("alice"), TestObjectRelation.CanView, Object);
        var right = SealedFgaTupleOperation.Of(User, TestObjectRelation.CanView, TestObjectId.Parse(Object.ToString()));

        left.Equals(right).ShouldBeTrue();
        (left == right).ShouldBeTrue();
        (left != right).ShouldBeFalse();
        left.GetHashCode().ShouldBe(right.GetHashCode());
    }

    [Fact]
    public void Differing_user_relation_or_object_is_unequal() {
        var op = SealedFgaTupleOperation.Of(User, TestObjectRelation.CanView, Object);

        (op == SealedFgaTupleOperation.Of(new TestUserId("bob"), TestObjectRelation.CanView, Object)).ShouldBeFalse();
        (op == SealedFgaTupleOperation.Of(User, TestObjectRelation.CanEdit, Object)).ShouldBeFalse();
        (op == SealedFgaTupleOperation.Of(User, TestObjectRelation.CanView, TestObjectId.New())).ShouldBeFalse();
    }

    [Fact]
    public void Hash_set_collapses_duplicates() {
        var set = new HashSet<SealedFgaTupleOperation> {
            SealedFgaTupleOperation.Of(User, TestObjectRelation.CanView, Object),
            SealedFgaTupleOperation.Of(User, TestObjectRelation.CanView, Object),
            SealedFgaTupleOperation.Of(User, TestObjectRelation.CanEdit, Object),
        };

        set.Count.ShouldBe(2);
    }

    [Fact]
    public void Default_instance_equals_itself_but_not_a_real_operation() {
        var op = SealedFgaTupleOperation.Of(User, TestObjectRelation.CanView, Object);

        (default(SealedFgaTupleOperation) == default(SealedFgaTupleOperation)).ShouldBeTrue();
        (default(SealedFgaTupleOperation) == op).ShouldBeFalse();
        Should.NotThrow(() => default(SealedFgaTupleOperation).GetHashCode());
    }
}
