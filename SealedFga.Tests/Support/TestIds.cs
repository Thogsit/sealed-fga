using System;
using System.ComponentModel;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SealedFga.AuthModel;

namespace SealedFga.Tests.Support;

// Hand-written strongly-typed IDs that mirror exactly what the source generator emits for a
// [SealedFgaTypeId] class (value type, OpenFgaTypeName, New/Parse, tuple string, value equality,
// and the nested EF/Type converters). Owned by the test project so the unit tests don't depend on
// the generator having run.

/// <summary>Guid-backed test ID (OpenFGA type <c>testobject</c>).</summary>
[TypeConverter(typeof(IdTypeConverter))]
public sealed class TestObjectId(Guid val) : ISealedFgaTypeId<TestObjectId>, IEquatable<TestObjectId> {
    public Guid Value { get; set; } = val;
    public static string OpenFgaTypeName => "testobject";
    public static TestObjectId New() => new(Guid.NewGuid());
    public static TestObjectId Parse(string val) => new(Guid.Parse(val));
    public string AsOpenFgaIdTupleString() => $"{OpenFgaTypeName}:{ToString()}";
    public override string ToString() => Value.ToString();
    public bool Equals(TestObjectId? other) => other is not null && Value.Equals(other.Value);
    public override bool Equals(object? obj) => obj is TestObjectId o && Equals(o);
    public override int GetHashCode() => Value.GetHashCode();

    public sealed class EfCoreValueConverter()
        : ValueConverter<TestObjectId, Guid>(id => id.Value, val => new TestObjectId(val));

    public sealed class IdTypeConverter()
        : GuidIdTypeConverter<TestObjectId>(g => new TestObjectId(g), Parse);
}

/// <summary>Guid-backed test ID used as a relation target/parent (OpenFGA type <c>testparent</c>).</summary>
[TypeConverter(typeof(IdTypeConverter))]
public sealed class TestParentId(Guid val) : ISealedFgaTypeId<TestParentId>, IEquatable<TestParentId> {
    public Guid Value { get; set; } = val;
    public static string OpenFgaTypeName => "testparent";
    public static TestParentId New() => new(Guid.NewGuid());
    public static TestParentId Parse(string val) => new(Guid.Parse(val));
    public string AsOpenFgaIdTupleString() => $"{OpenFgaTypeName}:{ToString()}";
    public override string ToString() => Value.ToString();
    public bool Equals(TestParentId? other) => other is not null && Value.Equals(other.Value);
    public override bool Equals(object? obj) => obj is TestParentId o && Equals(o);
    public override int GetHashCode() => Value.GetHashCode();

    public sealed class EfCoreValueConverter()
        : ValueConverter<TestParentId, Guid>(id => id.Value, val => new TestParentId(val));

    public sealed class IdTypeConverter()
        : GuidIdTypeConverter<TestParentId>(g => new TestParentId(g), Parse);
}

/// <summary>String-backed test ID (OpenFGA type <c>testuser</c>).</summary>
[TypeConverter(typeof(IdTypeConverter))]
public sealed class TestUserId(string val) : ISealedFgaTypeId<TestUserId>, IEquatable<TestUserId> {
    public string Value { get; set; } = val;
    public static string OpenFgaTypeName => "testuser";
    public static TestUserId New() => new("");
    public static TestUserId Parse(string val) => new(val);
    public string AsOpenFgaIdTupleString() => $"{OpenFgaTypeName}:{ToString()}";
    public override string ToString() => Value;
    public bool Equals(TestUserId? other) => other is not null && Value == other.Value;
    public override bool Equals(object? obj) => obj is TestUserId o && Equals(o);
    public override int GetHashCode() => Value.GetHashCode();

    public sealed class EfCoreValueConverter()
        : ValueConverter<TestUserId, string>(id => id.Value, val => new TestUserId(val));

    public sealed class IdTypeConverter() : StringIdTypeConverter<TestUserId>(Parse);
}
