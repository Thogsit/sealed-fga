using System;
using System.Globalization;
using SealedFga.AuthModel;
using SealedFga.Tests.Support;
using SealedFga.Util;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>Tests for <see cref="IdUtil" />, the ID type converters, and <see cref="SealedFgaUserset{T}" />.</summary>
[Collection(GlobalCollection.Name)]
public class IdUtilAndConverterTests {
    private sealed class UnregisteredId; // never registered in IdUtil

    [Fact]
    public void GetNameByIdType_returns_registered_open_fga_type_name()
        => IdUtil.GetNameByIdType(typeof(TestObjectId)).ShouldBe("testobject");

    [Fact]
    public void ParseId_round_trips_a_registered_type() {
        var g = Guid.NewGuid();
        var parsed = IdUtil.ParseId<TestObjectId>(g.ToString());
        parsed.Value.ShouldBe(g);
    }

    [Fact]
    public void ParseId_of_unregistered_type_throws()
        => Should.Throw<InvalidOperationException>(() => IdUtil.ParseId(typeof(UnregisteredId), "x"));

    [Fact]
    public void GuidIdTypeConverter_converts_both_directions() {
        var converter = new TestObjectId.IdTypeConverter();
        var g = Guid.NewGuid();

        converter.CanConvertFrom(null!, typeof(string)).ShouldBeTrue();
        converter.CanConvertFrom(null!, typeof(Guid)).ShouldBeTrue();
        converter.CanConvertTo(null!, typeof(Guid)).ShouldBeTrue();

        ((TestObjectId) converter.ConvertFrom(null!, CultureInfo.InvariantCulture, g.ToString())!).Value.ShouldBe(g);
        ((TestObjectId) converter.ConvertFrom(null!, CultureInfo.InvariantCulture, g)!).Value.ShouldBe(g);

        var id = new TestObjectId(g);
        converter.ConvertTo(null!, CultureInfo.InvariantCulture, id, typeof(string)).ShouldBe(g.ToString());
        converter.ConvertTo(null!, CultureInfo.InvariantCulture, id, typeof(Guid)).ShouldBe(g);
    }

    [Fact]
    public void StringIdTypeConverter_converts_both_directions() {
        var converter = new TestUserId.IdTypeConverter();

        converter.CanConvertFrom(null!, typeof(string)).ShouldBeTrue();
        converter.CanConvertTo(null!, typeof(string)).ShouldBeTrue();

        ((TestUserId) converter.ConvertFrom(null!, CultureInfo.InvariantCulture, "bob")!).Value.ShouldBe("bob");
        converter.ConvertTo(null!, CultureInfo.InvariantCulture, new TestUserId("bob"), typeof(string)).ShouldBe("bob");
    }

    private sealed class TestRelation(string v) : SealedFgaRelation(v), ISealedFgaRelation<TestUserId>;

    [Fact]
    public void Userset_renders_object_and_relation() {
        var userset = SealedFgaUserset<TestUserId>.From(new TestUserId("bob"), new TestRelation("member"));
        userset.AsOpenFgaIdTupleString().ShouldBe("testuser:bob#member");
    }
}
