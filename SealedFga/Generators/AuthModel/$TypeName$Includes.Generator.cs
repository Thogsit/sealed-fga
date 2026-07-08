using System.Linq;
using SealedFga.Models;
using SealedFga.Util;

namespace SealedFga.Generators.AuthModel;

/// <summary>
///     Generates the per-entity <c>{Entity}Includes</c> companion type: a set of <c>const string</c> members,
///     one per includable navigation property, so callers can eager-load related entities via
///     <c>[FgaAuthorize(..., Include = [nameof({Entity}Includes.Nav)])]</c> without raw strings.
/// </summary>
public static class TypeNameIncludesGenerator {
    /// <summary>
    ///     Builds the includes companion file for an entity, or returns <c>null</c> when the entity has no
    ///     includable navigation properties (mirroring how empty relation buckets are skipped).
    /// </summary>
    public static GeneratedFile? Generate(EntityToGenerateData entity) {
        if (entity.NavigationPropertyNames.Length == 0) {
            return null;
        }

        var className = entity.ClassName + "Includes";
        return new GeneratedFile(
            $"{className}.g.cs",
            $$"""
              /// <summary>
              ///     Valid navigation properties that can be eager-loaded (EF Include) for {{entity.ClassName}}.
              /// </summary>
              public static class {{className}}
              {
                  {{GetIncludeConstants()}}
              }
              """,
            namespaceName: entity.ClassNamespace
        );

        string GetIncludeConstants()
            => GeneratorUtil.BuildLinesWithIndent(
                entity.NavigationPropertyNames.Select(name => $"public const string {name} = \"{name}\";"),
                4
            );
    }
}
