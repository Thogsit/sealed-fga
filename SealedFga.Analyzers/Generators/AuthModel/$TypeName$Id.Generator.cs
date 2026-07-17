using System;
using System.Collections.Generic;
using SealedFga.Models;

namespace SealedFga.Generators.AuthModel;

internal static class TypeNameIdGenerator {
    public const string ParseMethodName = "Parse";
    public const string OpenFgaIdTupleStringMethodName = "AsOpenFgaIdTupleString";
    public const string OpenFgaTypeNamePropertyName = "OpenFgaTypeName";

    public static GeneratedFile Generate(IdClassToGenerateData idClassToGenerate)
        => new(
            $"{idClassToGenerate.ClassName}.partial.g.cs",
            $$"""

              /// <remarks>
              ///     As a value type, <c>default({{idClassToGenerate.ClassName}})</c> is representable and carries the
              ///     all-zero value ({{GetDefaultValueDescription(idClassToGenerate)}}). It never denotes an existing entity;
              ///     don't let uninitialized IDs flow into tuples or queries.
              /// </remarks>
              [TypeConverter(typeof(IdTypeConverter))]
              [JsonConverter(typeof(IdJsonConverter))]
              public readonly partial record struct {{idClassToGenerate.ClassName}} : ISealedFgaTypeId<{{idClassToGenerate.ClassName}}>
              {
                  /// <summary>
                  ///     The ID's value.
                  /// </summary>
                  public {{idClassToGenerate.UnderlyingType}} Value { get; }

                  /// <summary>
                  ///     Creates a new instance of {{idClassToGenerate.ClassName}} from the ID's "raw" type.
                  /// </summary>
                  /// <param name="val">The raw ID's value.</param>
                  public {{idClassToGenerate.ClassName}}({{idClassToGenerate.UnderlyingType}} val)
                  {
                      Value = val;
                  }

                  /// <inheritdoc />
                  public static string {{OpenFgaTypeNamePropertyName}} => "{{idClassToGenerate.TypeName}}";

                  {{GetNewMethodBlock(idClassToGenerate)}}

                  /// <inheritdoc />
                  public static {{idClassToGenerate.ClassName}} {{ParseMethodName}}(string val)
                  {
                      return new {{idClassToGenerate.ClassName}}({{GetParserFunction(idClassToGenerate, "val")}});
                  }

                  /// <inheritdoc />
                  public string {{OpenFgaIdTupleStringMethodName}}()
                      => $"{OpenFgaTypeName}:{ToString()}";

                  /// <inheritdoc />
                  public override string ToString()
                  {
                      return {{GetToStringExpression(idClassToGenerate)}};
                  }

                  /// <summary>
                  ///     Converts a <see cref="{{idClassToGenerate.ClassName}}" /> to a <see cref="{{idClassToGenerate.UnderlyingType}}" /> and vice versa for DB storage.
                  /// </summary>
                  public class EfCoreValueConverter() : ValueConverter<{{idClassToGenerate.ClassName}}, {{idClassToGenerate.UnderlyingType}}>(
                      id => id.Value,
                      val => new {{idClassToGenerate.ClassName}}(val));

                  /// <summary>
                  ///    Converts a <see cref="{{idClassToGenerate.ClassName}}" /> to its JSON representation and vice versa.
                  /// </summary>
                  public class IdJsonConverter() : {{GetJsonConverter(idClassToGenerate)}};

                  /// <summary>
                  ///    Converts a <see cref="{{idClassToGenerate.ClassName}}" /> to another compatible type and vice versa.
                  /// </summary>
                  public class IdTypeConverter() : {{GetTypeConverter(idClassToGenerate)}};
              }
              """,
            new HashSet<string>([
                    ..GetTypeDependentUsings(idClassToGenerate),
                    "System",
                    "Microsoft.EntityFrameworkCore.Storage.ValueConversion",
                    Settings.AuthModelNamespace,
                    Settings.UtilNamespace,
                    "System.ComponentModel",
                    "System.Text.Json.Serialization",
                ]
            ),
            idClassToGenerate.ClassNamespace
        );

    private static string GetTypeConverter(IdClassToGenerateData idClassToGenerate)
        => idClassToGenerate.Type switch {
            SealedFgaTypeIdType.Guid
                => $"GuidIdTypeConverter<{idClassToGenerate.ClassName}>(g => new {idClassToGenerate.ClassName}(g), s => {idClassToGenerate.ClassName}.Parse(s))",
            SealedFgaTypeIdType.String
                => $"StringIdTypeConverter<{idClassToGenerate.ClassName}>(s => {idClassToGenerate.ClassName}.Parse(s))",
            SealedFgaTypeIdType.Int
                => $"Int32IdTypeConverter<{idClassToGenerate.ClassName}>(v => new {idClassToGenerate.ClassName}(v), s => {idClassToGenerate.ClassName}.Parse(s), id => id.Value)",
            SealedFgaTypeIdType.Long
                => $"Int64IdTypeConverter<{idClassToGenerate.ClassName}>(v => new {idClassToGenerate.ClassName}(v), s => {idClassToGenerate.ClassName}.Parse(s), id => id.Value)",
            _ => throw new ArgumentOutOfRangeException(),
        };

    /// <summary>
    ///     The <c>New()</c> factory block, or a comment for integer-backed IDs where no synthetic
    ///     value is sensible. String/Guid IDs generate a value; integer IDs are database-assigned
    ///     (identity), so emitting <c>New()</c> returning e.g. <c>0</c> would hand out a dangerous
    ///     "unset" ID — the method is omitted instead. Continuation lines are indented to the
    ///     member column (4 spaces) so the substituted block lines up in the emitted source.
    /// </summary>
    private static string GetNewMethodBlock(IdClassToGenerateData idClassToGenerate) {
        if (idClassToGenerate.Type is SealedFgaTypeIdType.Int or SealedFgaTypeIdType.Long) {
            return string.Join("\n    ", new List<string> {
                    "// No New() is generated for integer-backed IDs: their values are database-assigned",
                    "// (identity); a synthetic value such as 0 would be a dangerous \"unset\" ID.",
                }
            );
        }

        return string.Join("\n    ", new List<string> {
                "/// <summary>",
                $"///     Creates a new instance of <see cref=\"{idClassToGenerate.ClassName}\"/> with a generated value.",
                "/// </summary>",
                $"public static {idClassToGenerate.ClassName} New()",
                "{",
                $"    return new {idClassToGenerate.ClassName}({GetNewFunction(idClassToGenerate)});",
                "}",
            }
        );
    }

    private static string GetJsonConverter(IdClassToGenerateData idClassToGenerate) {
        var strTypeConverter =
            $"JsonSimpleStringConverter<{idClassToGenerate.ClassName}>(s => {idClassToGenerate.ClassName}.Parse(s))";
#pragma warning disable CS8524
        return idClassToGenerate.Type switch
#pragma warning restore CS8524
        {
            SealedFgaTypeIdType.Guid => strTypeConverter,
            SealedFgaTypeIdType.String => strTypeConverter,
            SealedFgaTypeIdType.Int
                => $"JsonSimpleInt32Converter<{idClassToGenerate.ClassName}>(v => new {idClassToGenerate.ClassName}(v), id => id.Value)",
            SealedFgaTypeIdType.Long
                => $"JsonSimpleInt64Converter<{idClassToGenerate.ClassName}>(v => new {idClassToGenerate.ClassName}(v), id => id.Value)",
        };
    }

    private static HashSet<string> GetTypeDependentUsings(IdClassToGenerateData idClassToGenerate) {
        var usings = new HashSet<string>();

        // Numeric IDs parse/format culture-invariantly (CultureInfo.InvariantCulture).
        if (idClassToGenerate.Type is SealedFgaTypeIdType.Int or SealedFgaTypeIdType.Long) {
            usings.Add("System.Globalization");
        }

        return usings;
    }

    /// <summary>
    ///     The <c>ToString()</c> body. String-backed IDs must stay null-safe: <c>default(XId)</c>
    ///     leaves the <c>string</c> value <c>null</c>, and <c>ToString()</c> must never throw.
    /// </summary>
    private static string GetToStringExpression(IdClassToGenerateData idClassToGenerate) {
#pragma warning disable CS8524
        return idClassToGenerate.Type switch
#pragma warning restore CS8524
        {
            SealedFgaTypeIdType.Guid => "Value.ToString()",
            SealedFgaTypeIdType.String => "Value ?? \"\"",
            SealedFgaTypeIdType.Int => "Value.ToString(CultureInfo.InvariantCulture)",
            SealedFgaTypeIdType.Long => "Value.ToString(CultureInfo.InvariantCulture)",
        };
    }

    /// <summary>Human-readable description of the <c>default</c> instance's value for the hazard doc.</summary>
    private static string GetDefaultValueDescription(IdClassToGenerateData idClassToGenerate) {
#pragma warning disable CS8524
        return idClassToGenerate.Type switch
#pragma warning restore CS8524
        {
            SealedFgaTypeIdType.Guid => "<c>Guid.Empty</c>",
            SealedFgaTypeIdType.String => "a <c>null</c> string; <c>ToString()</c> maps it to <c>\"\"</c>",
            SealedFgaTypeIdType.Int => "<c>0</c>",
            SealedFgaTypeIdType.Long => "<c>0</c>",
        };
    }

    private static string GetParserFunction(IdClassToGenerateData idClassToGenerate, string attrName) {
#pragma warning disable CS8524
        return idClassToGenerate.Type switch
#pragma warning restore CS8524
        {
            SealedFgaTypeIdType.Guid => $"Guid.Parse({attrName})",
            SealedFgaTypeIdType.String => attrName,
            SealedFgaTypeIdType.Int => $"int.Parse({attrName}, CultureInfo.InvariantCulture)",
            SealedFgaTypeIdType.Long => $"long.Parse({attrName}, CultureInfo.InvariantCulture)",
        };
    }

    /// <summary>
    ///     The generated <c>New()</c> value expression. Only reached for String/Guid IDs;
    ///     integer-backed IDs omit <c>New()</c> entirely (see <see cref="GetNewMethodBlock" />).
    /// </summary>
    private static string GetNewFunction(IdClassToGenerateData idClassToGenerate)
        => idClassToGenerate.Type switch {
            SealedFgaTypeIdType.Guid => "Guid.NewGuid()",
            SealedFgaTypeIdType.String => "\"\"",
            _ => throw new ArgumentOutOfRangeException(nameof(idClassToGenerate)),
        };
}
