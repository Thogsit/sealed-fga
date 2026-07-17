using System;
using System.Linq;
using SealedFga.AuthModel;
using SealedFga.Fga;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>
///     Unit tests for <see cref="SealedFgaQueryOptions" />' mapping of typed contextual tuples to
///     the two SDK shapes (checked via InternalsVisibleTo): <c>Check</c>/<c>ListObjects</c> take
///     <c>List&lt;ClientTupleKey&gt;</c>, <c>BatchCheck</c> items take <c>ContextualTupleKeys</c>.
/// </summary>
public class QueryOptionsTests {
    /// <summary>Typed relation targeting <see cref="TestObjectId" />, mirroring generated relation classes.</summary>
    private sealed class TestObjectRelation(string val) : SealedFgaRelation(val), ISealedFgaRelation<TestObjectId>;

    /// <summary>Typed relation targeting <see cref="TestParentId" /> (for userset subjects).</summary>
    private sealed class TestParentRelation(string val) : SealedFgaRelation(val), ISealedFgaRelation<TestParentId>;

    private static readonly TestObjectRelation CanView = new("can_view");
    private static readonly TestParentRelation Member = new("Member");

    [Fact]
    public void Maps_contextual_tuples_to_both_sdk_shapes() {
        var user = TestUserId.Parse("alice");
        var obj = TestObjectId.New();
        var options = new SealedFgaQueryOptions {
            ContextualTuples = [SealedFgaContextualTuple.Of(user, CanView, obj)],
        };

        var clientTupleKeys = options.ToClientTupleKeys().ShouldNotBeNull();
        var single = clientTupleKeys.ShouldHaveSingleItem();
        single.User.ShouldBe("testuser:alice");
        single.Relation.ShouldBe("can_view");
        single.Object.ShouldBe(obj.AsOpenFgaIdTupleString());

        var contextualTupleKeys = options.ToContextualTupleKeys().ShouldNotBeNull();
        var batchSingle = contextualTupleKeys.TupleKeys.ShouldHaveSingleItem();
        batchSingle.User.ShouldBe("testuser:alice");
        batchSingle.Relation.ShouldBe("can_view");
        batchSingle.Object.ShouldBe(obj.AsOpenFgaIdTupleString());
    }

    [Fact]
    public void Maps_userset_subjects_to_hash_notation() {
        var parent = TestParentId.New();
        var obj = TestObjectId.New();
        var options = new SealedFgaQueryOptions {
            ContextualTuples = [
                SealedFgaContextualTuple.Of(SealedFgaUserset<TestParentId>.From(parent, Member), CanView, obj),
            ],
        };

        var single = options.ToClientTupleKeys().ShouldNotBeNull().ShouldHaveSingleItem();
        single.User.ShouldBe($"{parent.AsOpenFgaIdTupleString()}#Member");
    }

    [Fact]
    public void Null_or_empty_contextual_tuples_map_to_null() {
        new SealedFgaQueryOptions().ToClientTupleKeys().ShouldBeNull();
        new SealedFgaQueryOptions().ToContextualTupleKeys().ShouldBeNull();

        var empty = new SealedFgaQueryOptions { ContextualTuples = [] };
        empty.ToClientTupleKeys().ShouldBeNull();
        empty.ToContextualTupleKeys().ShouldBeNull();
    }

    [Fact]
    public void Default_constructed_contextual_tuple_is_rejected() {
        var options = new SealedFgaQueryOptions {
            ContextualTuples = [default],
        };

        Should.Throw<ArgumentException>(() => options.ToClientTupleKeys().ShouldNotBeNull().ToList());
        Should.Throw<ArgumentException>(() => options.ToContextualTupleKeys().ShouldNotBeNull());
    }

    [Fact]
    public void Of_rejects_null_parts() {
        var obj = TestObjectId.New();
        Should.Throw<ArgumentNullException>(() => SealedFgaContextualTuple.Of(null!, CanView, obj));
        Should.Throw<ArgumentNullException>(() => SealedFgaContextualTuple.Of(TestUserId.Parse("a"), null!, obj));
    }
}
