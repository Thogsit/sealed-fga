using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using SealedFga.Models;
using SealedFga.Util;

namespace SealedFga.Generators;

public static class SealedFgaInitGenerator {
    public static GeneratedFile Generate(ImmutableArray<IdClassToGenerateData> idClasses)
        => new(
            "SealedFgaInit.g.cs",
            $$"""
              /// <summary>
              ///     SealedFga static class for initialization and registration of SealedFGA type IDs.
              /// </summary>
              public static class SealedFgaInit {
                  /// <summary>
                  ///     Runs <see cref="Initialize" /> automatically when the consumer assembly is loaded,
                  ///     so consumers never have to call it themselves. Kept separate (and internal) from
                  ///     the public <see cref="Initialize" /> so tests can still register test-only IDs by
                  ///     calling <see cref="Initialize" /> directly (registration is idempotent).
                  /// </summary>
                  [ModuleInitializer]
                  internal static void AutoInitialize() => Initialize();

                  /// <summary>
                  ///     Initializes the SealedFga library by registering all SealedFGA type IDs.
                  /// </summary>
                  public static void Initialize() {
                      // Register all SealedFGA type IDs.
                      {{GetIdUtilInitializationBlock(idClasses)}}
                  }
              }
              """,
            new HashSet<string>([
                    "System.Runtime.CompilerServices",
                    Settings.UtilNamespace,
                ]
            )
        );

    private static string GetIdUtilInitializationBlock(ImmutableArray<IdClassToGenerateData> idClasses) {
        var idUtilInitializations = idClasses
           .SelectMany(idClass => {
                    var fullClassName = $"{idClass.ClassNamespace}.{idClass.ClassName}";
                    return new List<string>([
                            $"IdUtil.RegisterIdType(typeof({fullClassName}), \"{idClass.TypeName}\");",
                            $"IdUtil.RegisterIdTypeParseMethod(typeof({fullClassName}), {fullClassName}.Parse);",
                        ]
                    );
                }
            );

        return GeneratorUtil.BuildLinesWithIndent(
            idUtilInitializations,
            8
        );
    }
}
