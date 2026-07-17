using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using OpenFga.Language;
using OpenFga.Language.Model;
using SealedFga.Generators;
using SealedFga.Generators.AuthModel;
using SealedFga.Generators.Fga;
using SealedFga.Models;

namespace SealedFga;

[Generator]
public class SealedFgaSourceGenerator : IIncrementalGenerator {
    private static readonly DiagnosticDescriptor InvalidModelFgaFileRule = new(
        "SFGA001",
        "Invalid OpenFGA model file",
        "The OpenFGA model file could not be parsed: {0}",
        "Security",
        DiagnosticSeverity.Error,
        true
    );

    private static readonly DiagnosticDescriptor UnknownOpenFgaTypeNameRule = new(
        "SFGA002",
        "Unknown OpenFGA type name",
        "The [SealedFgaTypeId] attribute references an unknown OpenFGA type name: {0}. Please make sure the type name exists in your '*.fga' model file.",
        "Security",
        DiagnosticSeverity.Error,
        true
    );

    private static readonly DiagnosticDescriptor MultipleModelFilesRule = new(
        "SFGA003",
        "Multiple OpenFGA model files",
        "More than one '*.fga' model file was found. SealedFga supports exactly one authorization model file per project.",
        "Security",
        DiagnosticSeverity.Error,
        true
    );

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        // Filters for changes to any '*.fga' model file (auto-included as AdditionalFiles by the
        // package's build props). Collected so we can detect (and reject) multiple model files.
        var modelFgaProvider = context.AdditionalTextsProvider
                                      .Where(f => Path.GetExtension(f.Path)
                                                      .Equals(".fga", StringComparison.OrdinalIgnoreCase))
                                      .Select((f, _) => {
                                               var fileContent = f.GetText()?.ToString();
                                               AuthorizationModel? authModel = null;
                                               var parseErrors = ImmutableArray<ParseErrorInfo>.Empty;
                                               if (fileContent is not null) {
                                                   try {
                                                       var authModelResult =
                                                           OpenFgaFromDslTransformer.ParseDsl(fileContent);
                                                       if (authModelResult.IsFailure) {
                                                           // Line/column are already 0-based (the error
                                                           // listener stores line - 1), matching Roslyn's
                                                           // LinePosition.
                                                           parseErrors = authModelResult.Errors
                                                              .Select(e => new ParseErrorInfo(
                                                                       e.Message,
                                                                       e.Line?.Start ?? 0,
                                                                       e.Column?.Start ?? 0,
                                                                       Math.Max(e.Column?.End ?? 0, e.Column?.Start ?? 0)
                                                                   )
                                                               )
                                                              .ToImmutableArray();
                                                       } else {
                                                           authModel = authModelResult.AuthorizationModel;
                                                       }
                                                   } catch (OperationCanceledException) {
                                                       throw;
                                                   } catch (Exception ex) {
                                                       // The ANTLR tree walk can throw on some malformed
                                                       // inputs before the error listeners capture anything;
                                                       // surface that as a parse error instead of crashing
                                                       // the generator (CS8785).
                                                       parseErrors = [new ParseErrorInfo(ex.Message, 0, 0, 0)];
                                                   }
                                               }

                                               return new ModelFgaIncrementalChange {
                                                   FilePath = f.Path,
                                                   AuthorizationModel = authModel,
                                                   ParseErrors = parseErrors,
                                               };
                                           }
                                       )
                                      .Collect();

        // Filters for type declarations with the OpenFgaTypeIdAttribute. IDs are generated as
        // `readonly partial record struct`, so the consumer-side declaration is a record struct —
        // but the predicate stays permissive (any type declaration) so a stale `partial class XId`
        // still generates and surfaces as a clear "conflicting partial declarations" compile error
        // instead of silently generating nothing.
        var fgaTypeIdProvider = context.SyntaxProvider.ForAttributeWithMetadataName(
            Settings.SealedFgaTypeIdAttributeMetadataName,
            static (synNode, _) => synNode is TypeDeclarationSyntax,
            static (context, _) => {
                var typeDeclaration = (TypeDeclarationSyntax) context.TargetNode;
                var attribute = context.Attributes[0];
                return IdClassToGenerateData.From(
                    attribute,
                    typeDeclaration,
                    context.TargetSymbol
                );
            }
        ).Collect();

        // MSBuild property SealedFgaSplitRelationClasses (default true): whether relation constants
        // split into …Permissions/…Groups classes by casing, or emit as one …Relations class per type.
        // Only an explicit "false" disables the split — absent or unparseable values keep the default.
        var splitRelationClassesProvider = context.AnalyzerConfigOptionsProvider
                                                  .Select(static (provider, _) =>
                                                       !provider.GlobalOptions.TryGetValue(
                                                           "build_property.SealedFgaSplitRelationClasses",
                                                           out var value
                                                       )
                                                       || !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
                                                   );

        // Combine so that we are triggered when the model.fga file changes, a type with the
        // OpenFgaTypeIdAttribute is added, or the relation-split property changes
        var fgaRelatedChangesProvider = modelFgaProvider.Combine(fgaTypeIdProvider).Combine(splitRelationClassesProvider);

        // Register incremental model.fga based code gen
        context.RegisterSourceOutput(fgaRelatedChangesProvider, GenerateCodeOnFgaRelatedChange);

        // Filters for entity classes implementing ISealedFgaType<TId>. Independent of the model.fga /
        // ID-class pipeline: entity navigation info comes purely from the entity's own symbol.
        var entityProvider = context.SyntaxProvider.CreateSyntaxProvider(
            static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
            static (ctx, ct) => EntityToGenerateData.From(ctx, ct)
        ).Where(static entity => entity is not null).Collect();

        // Register per-entity {Entity}Includes code gen
        context.RegisterSourceOutput(entityProvider, GenerateEntityIncludes);

        // Register non-incremental code gen
        context.RegisterPostInitializationOutput(GenerateNonIncrementalSourceFiles);
    }

    private static void GenerateNonIncrementalSourceFiles(IncrementalGeneratorPostInitializationContext context) {
        var generatedFiles = new List<GeneratedFile>([
                SealedFgaExtensionsGenerator.Generate(),
                SealedFgaSaveChangesInterceptorGenerator.Generate(),
            ]
        );

        foreach (var genFile in generatedFiles) {
            context.AddSource(
                genFile.FileName,
                genFile.BuildFullFileContent()
            );
        }
    }

    private static void GenerateEntityIncludes(
        SourceProductionContext context,
        ImmutableArray<EntityToGenerateData?> entities
    ) {
        // A partial entity split across files would fire the syntax provider more than once; dedupe by
        // emitted file name so AddSource never receives a duplicate hint name.
        var emitted = new HashSet<string>();
        foreach (var entity in entities) {
            if (entity is null) {
                continue;
            }

            var includesFile = TypeNameIncludesGenerator.Generate(entity);
            if (includesFile is null || !emitted.Add(includesFile.FileName)) {
                continue;
            }

            context.AddSource(includesFile.FileName, includesFile.BuildFullFileContent());
        }
    }

    private static void GenerateCodeOnFgaRelatedChange(
        SourceProductionContext context,
        ((ImmutableArray<ModelFgaIncrementalChange> modelFiles, ImmutableArray<IdClassToGenerateData> idClasses),
            bool splitRelationClasses) fgaRelatedChangesWithOptions
    ) {
        var (fgaRelatedChanges, splitRelationClasses) = fgaRelatedChangesWithOptions;
        var modelFiles = fgaRelatedChanges.modelFiles;

        // No '*.fga' model file in the project: nothing to generate.
        if (modelFiles.Length == 0) {
            return;
        }

        // Exactly one model file is supported; reject and bail on more (else per-file generation would
        // emit duplicate hint names).
        if (modelFiles.Length > 1) {
            foreach (var modelFile in modelFiles) {
                context.ReportDiagnostic(Diagnostic.Create(
                        MultipleModelFilesRule,
                        Location.Create(modelFile.FilePath, default, default)
                    )
                );
            }
            return;
        }

        var modelFileChange = modelFiles[0];
        if (modelFileChange.AuthorizationModel is null) {
            if (modelFileChange.ParseErrors.IsDefaultOrEmpty) {
                context.ReportDiagnostic(Diagnostic.Create(
                        InvalidModelFgaFileRule,
                        Location.Create(modelFileChange.FilePath, default, default),
                        "the file could not be read"
                    )
                );
            } else {
                foreach (var error in modelFileChange.ParseErrors) {
                    var lineSpan = new LinePositionSpan(
                        new LinePosition(error.Line, error.ColumnStart),
                        new LinePosition(error.Line, error.ColumnEnd)
                    );
                    context.ReportDiagnostic(Diagnostic.Create(
                            InvalidModelFgaFileRule,
                            Location.Create(modelFileChange.FilePath, default, lineSpan),
                            error.Message
                        )
                    );
                }
            }

            return;
        }

        foreach (var idClassToGenerate in fgaRelatedChanges.idClasses) {
            AddGeneratedFilesForFgaType(context,
                modelFileChange.AuthorizationModel,
                idClassToGenerate,
                splitRelationClasses
            );
        }

        var generatedSealedFgaFile = SealedFgaInitGenerator.Generate(fgaRelatedChanges.idClasses);
        context.AddSource(generatedSealedFgaFile.FileName, generatedSealedFgaFile.BuildFullFileContent());
    }

    private static void AddGeneratedFilesForFgaType(
        SourceProductionContext context,
        AuthorizationModel authModel,
        IdClassToGenerateData idClassToGenerate,
        bool splitRelationClasses
    ) {
        // Check if the OpenFGA type name is valid
        if (authModel.TypeDefinitions.All(td => td.Type != idClassToGenerate.TypeName)) {
            context.ReportDiagnostic(Diagnostic.Create(
                    UnknownOpenFgaTypeNameRule,
                    idClassToGenerate.Location,
                    idClassToGenerate.TypeName
                )
            );
            return;
        }

        // Generate the partial record struct for the OpenFGA ID type
        var generatedIdFile = TypeNameIdGenerator.Generate(idClassToGenerate);
        context.AddSource(generatedIdFile.FileName, generatedIdFile.BuildFullFileContent());

        // Generate the relation types for the OpenFGA ID type
        var generatedRelationFiles = TypeNameRelationsGenerator.Generate(authModel, idClassToGenerate, splitRelationClasses);
        foreach (var generatedRelationsFile in generatedRelationFiles) {
            context.AddSource(generatedRelationsFile.FileName, generatedRelationsFile.BuildFullFileContent());
        }
    }
}
