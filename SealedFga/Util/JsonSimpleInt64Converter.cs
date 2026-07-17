using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SealedFga.Util;

/// <summary>
///     A JSON converter for <see cref="long" />-backed strongly-typed IDs. Serializes as a JSON
///     <b>number</b>; on read it tolerates either a JSON number or a numeric string, so payloads
///     produced with <see cref="JsonNumberHandling.WriteAsString" /> still round-trip. Consumers
///     whose JavaScript clients cannot hold values above 2^53 should opt into string number handling
///     on their serializer.
/// </summary>
/// <param name="constrFunc">A function that builds a <b>T</b> from a <see cref="long" />.</param>
/// <param name="valueFunc">A function returning the ID's underlying <see cref="long" /> value.</param>
/// <typeparam name="T">The ID type to convert to/from.</typeparam>
public class JsonSimpleInt64Converter<T>(Func<long, T> constrFunc, Func<T, long> valueFunc) : JsonConverter<T>
    where T : notnull {
    /// <inheritdoc />
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            ? constrFunc(long.Parse(reader.GetString()!, CultureInfo.InvariantCulture))
            : constrFunc(reader.GetInt64());

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteNumberValue(valueFunc(value));
}
