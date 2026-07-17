using System;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SealedFga.AuthModel;
using SealedFga.Util;

namespace SealedFga.Tests.Support;

// Hand-written strongly-typed IDs that mirror exactly what the source generator emits for a
// [SealedFgaTypeId] type (readonly record struct, OpenFgaTypeName, New/Parse, tuple string, and
// the nested EF/Type converters; equality comes from the record struct itself). Owned by the test
// project so the unit tests don't depend on the generator having run.

/// <summary>Guid-backed test ID (OpenFGA type <c>testobject</c>).</summary>
[TypeConverter(typeof(IdTypeConverter))]
public readonly record struct TestObjectId(Guid Value) : ISealedFgaTypeId<TestObjectId> {
    public static string OpenFgaTypeName => "testobject";
    public static TestObjectId New() => new(Guid.NewGuid());
    public static TestObjectId Parse(string val) => new(Guid.Parse(val));
    public string AsOpenFgaIdTupleString() => $"{OpenFgaTypeName}:{ToString()}";
    public override string ToString() => Value.ToString();

    public sealed class EfCoreValueConverter()
        : ValueConverter<TestObjectId, Guid>(id => id.Value, val => new TestObjectId(val));

    public sealed class IdTypeConverter()
        : GuidIdTypeConverter<TestObjectId>(g => new TestObjectId(g), Parse);
}

/// <summary>Guid-backed test ID used as a relation target/parent (OpenFGA type <c>testparent</c>).</summary>
[TypeConverter(typeof(IdTypeConverter))]
public readonly record struct TestParentId(Guid Value) : ISealedFgaTypeId<TestParentId> {
    public static string OpenFgaTypeName => "testparent";
    public static TestParentId New() => new(Guid.NewGuid());
    public static TestParentId Parse(string val) => new(Guid.Parse(val));
    public string AsOpenFgaIdTupleString() => $"{OpenFgaTypeName}:{ToString()}";
    public override string ToString() => Value.ToString();

    public sealed class EfCoreValueConverter()
        : ValueConverter<TestParentId, Guid>(id => id.Value, val => new TestParentId(val));

    public sealed class IdTypeConverter()
        : GuidIdTypeConverter<TestParentId>(g => new TestParentId(g), Parse);
}

/// <summary>String-backed test ID (OpenFGA type <c>testuser</c>).</summary>
[TypeConverter(typeof(IdTypeConverter))]
public readonly record struct TestUserId(string Value) : ISealedFgaTypeId<TestUserId> {
    public static string OpenFgaTypeName => "testuser";
    public static TestUserId New() => new("");
    public static TestUserId Parse(string val) => new(val);
    public string AsOpenFgaIdTupleString() => $"{OpenFgaTypeName}:{ToString()}";
    public override string ToString() => Value ?? "";

    public sealed class EfCoreValueConverter()
        : ValueConverter<TestUserId, string>(id => id.Value, val => new TestUserId(val));

    public sealed class IdTypeConverter() : StringIdTypeConverter<TestUserId>(Parse);
}

// Int/long-backed mirrors: like the generator, integer IDs omit New() (their values are
// database-assigned) and serialize as JSON numbers.

/// <summary>Int-backed test ID (OpenFGA type <c>testchannel</c>).</summary>
[TypeConverter(typeof(IdTypeConverter))]
[JsonConverter(typeof(IdJsonConverter))]
public readonly record struct TestChannelId(int Value) : ISealedFgaTypeId<TestChannelId> {
    public static string OpenFgaTypeName => "testchannel";
    public static TestChannelId Parse(string val) => new(int.Parse(val, CultureInfo.InvariantCulture));
    public string AsOpenFgaIdTupleString() => $"{OpenFgaTypeName}:{ToString()}";
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    public sealed class EfCoreValueConverter()
        : ValueConverter<TestChannelId, int>(id => id.Value, val => new TestChannelId(val));

    public sealed class IdJsonConverter()
        : JsonSimpleInt32Converter<TestChannelId>(v => new TestChannelId(v), id => id.Value);

    public sealed class IdTypeConverter()
        : Int32IdTypeConverter<TestChannelId>(v => new TestChannelId(v), Parse, id => id.Value);
}

/// <summary>Long-backed test ID (OpenFGA type <c>testbig</c>).</summary>
[TypeConverter(typeof(IdTypeConverter))]
[JsonConverter(typeof(IdJsonConverter))]
public readonly record struct TestBigId(long Value) : ISealedFgaTypeId<TestBigId> {
    public static string OpenFgaTypeName => "testbig";
    public static TestBigId Parse(string val) => new(long.Parse(val, CultureInfo.InvariantCulture));
    public string AsOpenFgaIdTupleString() => $"{OpenFgaTypeName}:{ToString()}";
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    public sealed class EfCoreValueConverter()
        : ValueConverter<TestBigId, long>(id => id.Value, val => new TestBigId(val));

    public sealed class IdJsonConverter()
        : JsonSimpleInt64Converter<TestBigId>(v => new TestBigId(v), id => id.Value);

    public sealed class IdTypeConverter()
        : Int64IdTypeConverter<TestBigId>(v => new TestBigId(v), Parse, id => id.Value);
}
