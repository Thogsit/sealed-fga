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

        var relPermissionsClassName = PermissionsClassName(idClassToGenerate);
        var relGroupsClassName = GroupsClassName(idClassToGenerate);
        var permissionRelations = relationNames.Relations!.Keys.Where(IsPermissionRelation).ToList();
        var groupRelations = relationNames.Relations!.Keys.Where(IsGroupRelation).ToList();
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
            relNames.SelectMany(rel => new[] {
                    $"/// <summary>The <c>{rel}</c> relation.</summary>",
                    $"public static readonly {className} {rel} = new {className}(\"{rel}\");",
                }
            ),
            4
        );

    /// <summary>The <c>{ClassName}Permissions</c> class name (lowercase-first relations).</summary>
    private static string PermissionsClassName(IdClassToGenerateData idClass) => idClass.ClassName + "Permissions";

    /// <summary>The <c>{ClassName}Groups</c> class name (uppercase-first relations).</summary>
    private static string GroupsClassName(IdClassToGenerateData idClass) => idClass.ClassName + "Groups";

    /// <summary>The single <c>{ClassName}Relations</c> class name (used when the split is disabled).</summary>
    private static string RelationsClassName(IdClassToGenerateData idClass) => idClass.ClassName + "Relations";

    /// <summary>A permission relation: <c>can_*</c>-style, lowercase first letter → <c>…Permissions</c>.</summary>
    private static bool IsPermissionRelation(string rel) => rel.Length > 0 && char.IsLower(rel[0]);

    /// <summary>A group/role relation: uppercase first letter (e.g. <c>Member</c>) → <c>…Groups</c>.</summary>
    private static bool IsGroupRelation(string rel) => rel.Length > 0 && char.IsUpper(rel[0]);

    /// <summary>
    ///     Resolves the generated relation class to use when wrapping an arbitrary relation string for a
    ///     check against this type (the SealedFGA dispatcher's need). Returns the <b>simple</b> class name
    ///     of a relation class that <see cref="Generate" /> actually emitted for the type, or <c>null</c>
    ///     when the type has no relation class at all (not checkable — the dispatcher then omits it).
    ///     <para>
    ///         Which class is fine because every generated relation class wraps the raw string identically
    ///         (<c>FromOpenFgaString</c>) and a check only reads it back via <c>AsOpenFgaString()</c>. The
    ///         selection mirrors <see cref="Generate" /> exactly (same predicates / names) so the dispatcher
    ///         can never name a class that was not emitted — e.g. a groups-only type resolves to
    ///         <c>…Groups</c>, which is the case the hand-written switch used to get wrong.
    ///     </para>
    /// </summary>
    public static string? ResolveDispatchRelationClassName(
        AuthorizationModel authModel,
        IdClassToGenerateData idClass,
        bool splitRelationClasses
    ) {
        var typeDef = authModel.TypeDefinitions.FirstOrDefault(td => td.Type == idClass.TypeName);
        var relations = typeDef?.Relations?.Keys.ToList();
        if (relations is null || relations.Count == 0) return null;

        if (!splitRelationClasses) return RelationsClassName(idClass);

        if (relations.Any(IsPermissionRelation)) return PermissionsClassName(idClass);
        if (relations.Any(IsGroupRelation)) return GroupsClassName(idClass);
        return null;
    }
}
