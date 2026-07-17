using System;
using System.Globalization;
using System.Text.Json;
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

    [Fact]
    public void Int_backed_id_round_trips_parse_tuple_string_and_registration() {
        var id = TestChannelId.Parse("42");
        id.Value.ShouldBe(42);
        id.ToString().ShouldBe("42");
        id.AsOpenFgaIdTupleString().ShouldBe("testchannel:42");
        IdUtil.GetNameByIdType(typeof(TestChannelId)).ShouldBe("testchannel");
        IdUtil.ParseId<TestChannelId>("7").Value.ShouldBe(7);
    }

    [Fact]
    public void Int32IdTypeConverter_converts_string_and_int_both_directions() {
        var converter = new TestChannelId.IdTypeConverter();

        converter.CanConvertFrom(null!, typeof(string)).ShouldBeTrue();
        converter.CanConvertFrom(null!, typeof(int)).ShouldBeTrue();
        converter.CanConvertTo(null!, typeof(int)).ShouldBeTrue();

        ((TestChannelId) converter.ConvertFrom(null!, CultureInfo.InvariantCulture, "13")!).Value.ShouldBe(13);
        ((TestChannelId) converter.ConvertFrom(null!, CultureInfo.InvariantCulture, 13)!).Value.ShouldBe(13);

        var id = new TestChannelId(99);
        converter.ConvertTo(null!, CultureInfo.InvariantCulture, id, typeof(string)).ShouldBe("99");
        converter.ConvertTo(null!, CultureInfo.InvariantCulture, id, typeof(int)).ShouldBe(99);
    }

    [Fact]
    public void Int64IdTypeConverter_converts_string_int_and_long() {
        var converter = new TestBigId.IdTypeConverter();

        converter.CanConvertFrom(null!, typeof(long)).ShouldBeTrue();
        converter.CanConvertFrom(null!, typeof(int)).ShouldBeTrue();
        converter.CanConvertTo(null!, typeof(long)).ShouldBeTrue();

        ((TestBigId) converter.ConvertFrom(null!, CultureInfo.InvariantCulture, "5000000000")!).Value
            .ShouldBe(5000000000L);
        ((TestBigId) converter.ConvertFrom(null!, CultureInfo.InvariantCulture, 42L)!).Value.ShouldBe(42L);

        var id = new TestBigId(5000000000L);
        converter.ConvertTo(null!, CultureInfo.InvariantCulture, id, typeof(long)).ShouldBe(5000000000L);
        converter.ConvertTo(null!, CultureInfo.InvariantCulture, id, typeof(string)).ShouldBe("5000000000");
    }

    [Fact]
    public void Int_backed_ids_serialize_as_json_numbers_and_round_trip() {
        JsonSerializer.Serialize(new TestChannelId(42)).ShouldBe("42");
        JsonSerializer.Deserialize<TestChannelId>("42").Value.ShouldBe(42);
        // Tolerant read: a numeric string still deserializes.
        JsonSerializer.Deserialize<TestChannelId>("\"42\"").Value.ShouldBe(42);

        JsonSerializer.Serialize(new TestBigId(5000000000L)).ShouldBe("5000000000");
        JsonSerializer.Deserialize<TestBigId>("5000000000").Value.ShouldBe(5000000000L);
    }
}
