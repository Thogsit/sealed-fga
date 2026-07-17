using System;
using System.ComponentModel;
using System.Globalization;

namespace SealedFga.AuthModel;

/// <summary>
///     Converts between <see cref="long" /> and a strongly-typed ID for use with SealedFGA entities.
/// </summary>
/// <typeparam name="TId">The strongly-typed ID type.</typeparam>
/// <param name="constrFunc">A function to construct the ID from a <see cref="long" />.</param>
/// <param name="parseFunc">A function to parse the ID from a <see cref="string" />.</param>
/// <param name="valueFunc">A function returning the ID's underlying <see cref="long" /> value.</param>
public class Int64IdTypeConverter<TId>(Func<long, TId> constrFunc, Func<string, TId> parseFunc, Func<TId, long> valueFunc)
    : TypeConverter where TId : struct, ISealedFgaTypeId<TId> {
    /// <inheritdoc />
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        => sourceType == typeof(string)
           || sourceType == typeof(long)
           || sourceType == typeof(int)
           || base.CanConvertFrom(context, sourceType);

    /// <inheritdoc />
    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
        => destinationType == typeof(string)
           || destinationType == typeof(long)
           || base.CanConvertTo(context, destinationType);

    /// <inheritdoc />
    public override object? ConvertFrom(
        ITypeDescriptorContext? context,
        CultureInfo? culture,
        object value
    ) {
        return value switch {
            string strValue => parseFunc(strValue),
            long longValue => constrFunc(longValue),
            int intValue => constrFunc(intValue),
            _ => base.ConvertFrom(context, culture, value),
        };
    }

    /// <inheritdoc />
    public override object? ConvertTo(
        ITypeDescriptorContext? context,
        CultureInfo? culture,
        object? value,
        Type destinationType
    ) {
        return value switch {
            TId tValue when destinationType == typeof(string) => tValue.ToString(),
            TId tValue when destinationType == typeof(long) => valueFunc(tValue),
            _ => base.ConvertTo(context, culture, value, destinationType),
        };
    }
}
