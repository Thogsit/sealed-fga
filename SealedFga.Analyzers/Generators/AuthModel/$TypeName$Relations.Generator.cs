using System.Collections.Generic;
using System.Linq;
using OpenFga.Language.Model;
using SealedFga.Models;
using SealedFga.Util;

namespace SealedFga.Generators.AuthModel;

internal static class TypeNameRelationsGenerator {
    private static GeneratedFile BuildRelationFile(
        string classNamespace,
        string className,
        string idClassName,
        List<string> relNames
    ) => new(
        $"{className}.g.cs",
        $$"""
          /// <summary>
          ///     Represents a set of strongly-typed SealedFGA relations for the {{className}} entity.
          /// </summary>
          public class {{className}}(string val)
          : SealedFgaRelation(val), ISealedFgaRelation<{{idClassName}}>
          {
              {{GetEnumFields(className, relNames)}}

              /// <summary>
              ///     Creates a <see cref="{{className}}"/> from an OpenFGA relation string.
              /// </summary>
              /// <param name="openFgaString">The OpenFGA relation string.</param>
              /// <returns>A new <see cref="{{className}}"/> instance.</returns>
              public static {{className}} FromOpenFgaString(string openFgaString) => new {{className}}(openFgaString);
          }
          """,
        new HashSet<string>([
                Settings.PackageNamespace,
                Settings.AuthModelNamespace,
            ]
        ),
        classNamespace
    );

    /// <summary>
    ///     Generates the relation-constant classes for one ID type. With
    ///     <paramref name="splitRelationClasses" /> (the default), relations split by casing into
    ///     <c>{ClassName}Permissions</c> (lowercase first letter, e.g. <c>can_view</c>) and
    ///     <c>{ClassName}Groups</c> (uppercase, e.g. <c>Member</c>) — a purely organizational split.
    ///     With the split disabled (MSBuild property <c>SealedFgaSplitRelationClasses=false</c>), all
    ///     relations land in a single <c>{ClassName}Relations</c> class.
    /// </summary>
    public static List<GeneratedFile> Generate(
        AuthorizationModel authModel,
        IdClassToGenerateData idClassToGenerate,
        bool splitRelationClasses
    ) {
        var relationFiles = new List<GeneratedFile>();
        var relationNames = authModel.TypeDefinitions.FirstOrDefault(td => td.Type == idClassToGenerate.TypeName);
        if (relationNames is null) return relationFiles;

        if (!splitRelationClasses) {
            var allRelations = relationNames.Relations!.Keys.ToList();
            if (allRelations.Count > 0) {
                relationFiles.Add(
                    BuildRelationFile(
                        idClassToGenerate.ClassNamespace,
                        idClassToGenerate.ClassName + "Relations",
                        idClassToGenerate.ClassName,
                        allRelations
                    )
                );
            }

            return relationFiles;
        }

        var relPermissionsClassName = idClassToGenerate.ClassName + "Permissions";
        var relGroupsClassName = idClassToGenerate.ClassName + "Groups";
        var permissionRelations = relationNames.Relations!.Keys.Where(rel => char.IsLower(rel[0])).ToList();
        var groupRelations = relationNames.Relations!.Keys.Where(rel => char.IsUpper(rel[0])).ToList();
        if (permissionRelations.Count > 0) {
            relationFiles.Add(
                BuildRelationFile(
                    idClassToGenerate.ClassNamespace,
                    relPermissionsClassName,
                    idClassToGenerate.ClassName,
                    permissionRelations
                )
            );
        }

        if (groupRelations.Count > 0) {
            relationFiles.Add(
                BuildRelationFile(
                    idClassToGenerate.ClassNamespace,
                    relGroupsClassName,
                    idClassToGenerate.ClassName,
                    groupRelations
                )
            );
        }

        return relationFiles;
    }

    private static string GetEnumFields(string className, List<string> relNames)
        => GeneratorUtil.BuildLinesWithIndent(
            relNames.Select(rel => $"public static readonly {className} {rel} = new {className}(\"{rel}\");"),
            4
        );
}
