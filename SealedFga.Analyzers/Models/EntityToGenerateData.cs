using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SealedFga.Util;

namespace SealedFga.Models;

/// <summary>
///     Describes a SealedFGA entity (a class implementing <c>ISealedFgaType&lt;TId&gt;</c>) and the set of
///     navigation properties on it that can be eager-loaded via EF <c>Include</c>. Used to generate the
///     per-entity <c>{Entity}Includes</c> companion type.
/// </summary>
/// <remarks>
///     Value-equatable (including the navigation list) so the incremental generator can cache correctly
///     and only re-run when the relevant shape actually changes.
/// </remarks>
internal sealed class EntityToGenerateData(
    string className,
    string classNamespace,
    ImmutableArray<string> navigationPropertyNames
) : IEquatable<EntityToGenerateData> {
    private const string SealedFgaTypeMetadataName = "ISealedFgaType`1";
    private const string SealedFgaTypeIdAttributeName = "SealedFgaTypeIdAttribute";
    private const string EnumerableMetadataName = "IEnumerable`1";
    private const string EnumerableNamespace = "System.Collections.Generic";

    public string ClassName { get; } = className;
    public string ClassNamespace { get; } = classNamespace;
    public ImmutableArray<string> NavigationPropertyNames { get; } = navigationPropertyNames;

    /// <summary>
    ///     Builds an <see cref="EntityToGenerateData" /> from a class declaration, or returns <c>null</c> when the
    ///     class is not a SealedFGA entity.
    /// </summary>
    public static EntityToGenerateData? From(GeneratorSyntaxContext ctx, CancellationToken ct) {
        var classDeclaration = (ClassDeclarationSyntax) ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(classDeclaration, ct) is not INamedTypeSymbol symbol) {
            return null;
        }

        // Only classes implementing ISealedFgaType<TId> are FGA entities.
        if (!ImplementsAuthModelInterface(symbol, SealedFgaTypeMetadataName)) {
            return null;
        }

        var navNames = symbol.GetMembers()
                             .OfType<IPropertySymbol>()
                             .Where(IsIncludableNavigation)
                             .Select(p => p.Name)
                             .ToImmutableArray();

        return new EntityToGenerateData(
            symbol.Name,
            symbol.ContainingNamespace.FullName(),
            navNames
        );
    }

    /// <summary>
    ///     Broad EF-navigation heuristic: a public instance property whose (element) type is a user-defined
    ///     reference type. Deliberately excludes primitives, <c>string</c>, FK id holders
    ///     (<c>ISealedFgaTypeId&lt;&gt;</c>), and BCL types (<c>System.*</c> / <c>Microsoft.*</c>).
    /// </summary>
    private static bool IsIncludableNavigation(IPropertySymbol property) {
        if (property.IsStatic
            || property.IsIndexer
            || property.DeclaredAccessibility != Accessibility.Public
            || property.GetMethod is not { DeclaredAccessibility: Accessibility.Public }) {
            return false;
        }

        var elementType = GetElementType(property.Type);

        if (elementType.SpecialType == SpecialType.System_String
            || elementType.IsValueType
            || elementType.TypeKind is not (TypeKind.Class or TypeKind.Interface)) {
            return false;
        }

        // FK id-typed properties (including the entity's own Id) are relationship keys, not navigations.
        // Detected via the [SealedFgaTypeId] attribute rather than the ISealedFgaTypeId<> interface: that
        // interface is added by a generated partial, which is not visible to this analysis compilation.
        if (HasSealedFgaTypeIdAttribute(elementType)) {
            return false;
        }

        var ns = elementType.ContainingNamespace.FullName();
        return !ns.StartsWith("System", StringComparison.Ordinal)
               && !ns.StartsWith("Microsoft", StringComparison.Ordinal);
    }

    /// <summary>
    ///     For a collection property (array or <c>IEnumerable&lt;T&gt;</c>) returns the element type; otherwise the
    ///     type itself.
    /// </summary>
    private static ITypeSymbol GetElementType(ITypeSymbol type) {
        if (type is IArrayTypeSymbol array) {
            return array.ElementType;
        }

        var enumerable = AllTypeAndInterfaces(type)
           .FirstOrDefault(i => i.MetadataName == EnumerableMetadataName
                                && i.ContainingNamespace.FullName() == EnumerableNamespace
            );

        return enumerable is { TypeArguments.Length: 1 } ? enumerable.TypeArguments[0] : type;
    }

    private static IEnumerable<INamedTypeSymbol> AllTypeAndInterfaces(ITypeSymbol type) {
        if (type is INamedTypeSymbol named) {
            yield return named;
        }

        foreach (var i in type.AllInterfaces) {
            yield return i;
        }
    }

    private static bool ImplementsAuthModelInterface(ITypeSymbol type, string metadataName)
        => type.AllInterfaces.Any(i => i.MetadataName == metadataName
                                       && i.ContainingNamespace.FullName() == Settings.AuthModelNamespace
            );

    private static bool HasSealedFgaTypeIdAttribute(ITypeSymbol type)
        => type.GetAttributes()
               .Any(a => a.AttributeClass is { Name: SealedFgaTypeIdAttributeName } attr
                         && attr.ContainingNamespace.FullName() == Settings.AttributesNamespace
                );

    public bool Equals(EntityToGenerateData? other)
        => other is not null
           && ClassName == other.ClassName
           && ClassNamespace == other.ClassNamespace
           && NavigationPropertyNames.SequenceEqual(other.NavigationPropertyNames);

    public override bool Equals(object? obj) => Equals(obj as EntityToGenerateData);

    public override int GetHashCode() {
        unchecked {
            var hash = ClassName.GetHashCode();
            hash = (hash * 397) ^ ClassNamespace.GetHashCode();
            foreach (var name in NavigationPropertyNames) {
                hash = (hash * 397) ^ name.GetHashCode();
            }

            return hash;
        }
    }
}
