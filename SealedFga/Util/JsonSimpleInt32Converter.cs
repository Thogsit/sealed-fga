using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SealedFga.Util;

/// <summary>
///     A JSON converter for <see cref="int" />-backed strongly-typed IDs. Serializes as a JSON
///     <b>number</b> (the natural representation of an integer key); on read it tolerates either a
///     JSON number or a numeric string, so payloads produced with
///     <see cref="JsonNumberHandling.WriteAsString" /> still round-trip.
/// </summary>
/// <param name="constrFunc">A function that builds a <b>T</b> from an <see cref="int" />.</param>
/// <param name="valueFunc">A function returning the ID's underlying <see cref="int" /> value.</param>
/// <typeparam name="T">The ID type to convert to/from.</typeparam>
public class JsonSimpleInt32Converter<T>(Func<int, T> constrFunc, Func<T, int> valueFunc) : JsonConverter<T>
    where T : notnull {
    /// <inheritdoc />
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            ? constrFunc(int.Parse(reader.GetString()!, CultureInfo.InvariantCulture))
            : constrFunc(reader.GetInt32());

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteNumberValue(valueFunc(value));
}
