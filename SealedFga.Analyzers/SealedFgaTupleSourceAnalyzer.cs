using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SealedFga;

/// <summary>
///     Reports <c>SFGA004</c> when an entity implements <c>ISealedFgaTupleSource</c> and also
///     declares <c>[SealedFgaRelation]</c> / <c>[SealedFgaJoinRelation]</c>. A tuple source owns
///     <b>all</b> of its entity's tuples — the attribute mechanisms are special cases of a tuple
///     source, and mixing them would fork the delete semantics (the tuple-source diff replaces the
///     <c>DeleteAllForObject</c> purge fence that attribute-annotated entities rely on).
///     The SaveChanges processor enforces the same rule at runtime as a backstop for compilations
///     that don't run analyzers.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class SealedFgaTupleSourceAnalyzer : DiagnosticAnalyzer {
    internal static readonly DiagnosticDescriptor TupleSourceMixedWithRelationAttributesRule = new(
        "SFGA004",
        "Tuple source mixed with relation attributes",
        "Entity '{0}' implements ISealedFgaTupleSource and also declares {1}. A tuple source owns all "
        + "of its entity's tuples; emit them from DesiredTuples() and remove the attribute.",
        "Security",
        DiagnosticSeverity.Error,
        true
    );

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => [TupleSourceMixedWithRelationAttributesRule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context) {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static compilationContext => {
                var tupleSource = compilationContext.Compilation
                                                    .GetTypeByMetadataName(Settings.SealedFgaTupleSourceInterfaceMetadataName);
                var relationAttribute = compilationContext.Compilation
                                                          .GetTypeByMetadataName(Settings.SealedFgaRelationAttributeMetadataName);
                var joinRelationAttribute = compilationContext.Compilation
                                                              .GetTypeByMetadataName(Settings.SealedFgaJoinRelationAttributeMetadataName);
                if (tupleSource is null) {
                    return;
                }

                compilationContext.RegisterSymbolAction(
                    symbolContext => AnalyzeNamedType(
                        symbolContext,
                        tupleSource,
                        relationAttribute,
                        joinRelationAttribute
                    ),
                    SymbolKind.NamedType
                );
            }
        );
    }

    /// <summary>
    ///     Checks one named type: implements the tuple-source interface → must carry neither relation
    ///     attribute shape. Reported once per offending type, at its identifier.
    /// </summary>
    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        INamedTypeSymbol tupleSource,
        INamedTypeSymbol? relationAttribute,
        INamedTypeSymbol? joinRelationAttribute
    ) {
        var type = (INamedTypeSymbol) context.Symbol;
        if (type.TypeKind != TypeKind.Class
            || !type.AllInterfaces.Contains(tupleSource, SymbolEqualityComparer.Default)) {
            return;
        }

        var hasJoinRelation = joinRelationAttribute is not null
                              && type.GetAttributes()
                                     .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, joinRelationAttribute));
        var hasScalarRelation = relationAttribute is not null
                                && type.GetMembers()
                                       .OfType<IPropertySymbol>()
                                       .Any(p => p.GetAttributes()
                                                  .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, relationAttribute))
                                        );

        if (!hasJoinRelation && !hasScalarRelation) {
            return;
        }

        var offendingAttributes = (hasScalarRelation, hasJoinRelation) switch {
            (true, true) => "[SealedFgaRelation] and [SealedFgaJoinRelation]",
            (true, false) => "[SealedFgaRelation]",
            _ => "[SealedFgaJoinRelation]",
        };

        context.ReportDiagnostic(Diagnostic.Create(
                TupleSourceMixedWithRelationAttributesRule,
                type.Locations.FirstOrDefault(),
                type.Name,
                offendingAttributes
            )
        );
    }
}
