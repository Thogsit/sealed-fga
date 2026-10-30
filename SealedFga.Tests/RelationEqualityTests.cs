using System.Collections.Generic;
using SealedFga.AuthModel;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>
///     Value-equality contract of <see cref="SealedFgaRelation" />: a relation parsed from its string equals the
///     generated constant, so relations work as set and dictionary keys.
/// </summary>
public class RelationEqualityTests {
    private sealed class OtherRelation(string val) : SealedFgaRelation(val), ISealedFgaRelation<TestObjectId>;

    [Fact]
    public void Same_class_and_value_is_equal_across_instances() {
        var parsed = new TestObjectRelation("can_view");

        parsed.Equals(TestObjectRelation.CanView).ShouldBeTrue();
        (parsed == TestObjectRelation.CanView).ShouldBeTrue();
        (parsed != TestObjectRelation.CanView).ShouldBeFalse();
        parsed.GetHashCode().ShouldBe(TestObjectRelation.CanView.GetHashCode());
    }

    [Fact]
    public void Differing_value_is_unequal() {
        (TestObjectRelation.CanView == TestObjectRelation.CanEdit).ShouldBeFalse();
        TestObjectRelation.CanView.Equals((object) TestObjectRelation.CanEdit).ShouldBeFalse();
    }

    [Fact]
    public void Comparison_is_case_sensitive() {
        (new TestObjectRelation("CAN_VIEW") == TestObjectRelation.CanView).ShouldBeFalse();
    }

    [Fact]
    public void Same_value_in_another_relation_class_is_unequal() {
        // can_view on one object type is a different relation from can_view on another.
        var other = new OtherRelation("can_view");

        TestObjectRelation.CanView.Equals(other).ShouldBeFalse();
        ((SealedFgaRelation) TestObjectRelation.CanView == other).ShouldBeFalse();
    }

    [Fact]
    public void Null_compares_unequal_and_null_equals_null() {
        TestObjectRelation? none = null;

        (TestObjectRelation.CanView == none).ShouldBeFalse();
        (none == TestObjectRelation.CanView).ShouldBeFalse();
        (none == null).ShouldBeTrue();
        TestObjectRelation.CanView.Equals(null).ShouldBeFalse();
    }

    [Fact]
    public void Hash_set_collapses_equal_relations() {
        var set = new HashSet<TestObjectRelation> {
            TestObjectRelation.CanView,
            new("can_view"),
            TestObjectRelation.CanEdit,
        };

        set.Count.ShouldBe(2);
        set.Contains(new TestObjectRelation("can_edit")).ShouldBeTrue();
    }

    [Fact]
    public void Interface_typed_dictionary_keys_match_by_value() {
        var byRelation = new Dictionary<ISealedFgaRelation<TestObjectId>, bool> { [TestObjectRelation.CanView] = true };

        byRelation.ContainsKey(new TestObjectRelation("can_view")).ShouldBeTrue();
    }
}
