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
using SealedFga.Attributes;
using SealedFga.Generators;
using SealedFga.Generators.AuthModel;
using SealedFga.Generators.Fga;
using SealedFga.Models;

namespace SealedFga;

[Generator]
public class SealedFgaSourceGenerator : IIncrementalGenerator {
    private static readonly DiagnosticDescriptor InvalidModelFgaFileRule = new(
        "SFGA001",
        "Invalid model.fga file",
        "The model.fga file could not be parsed correctly",
        "Security",
        DiagnosticSeverity.Error,
        true
    );

    private static readonly DiagnosticDescriptor UnknownOpenFgaTypeNameRule = new(
        "SFGA002",
        "Unknown OpenFGA type name",
        "The OpenFgaTypeId attribute references an unknown OpenFGA type name: {0}. Please make sure the type name exists in the 'model.fga' file.",
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
                                               if (fileContent is not null) {
                                                   var authModelResult =
                                                       OpenFgaFromDslTransformer.ParseDsl(fileContent);
                                                   if (authModelResult.IsFailure) {
                                                       // TODO: Error handling?
                                                   } else {
                                                       authModel = authModelResult.AuthorizationModel;
                                                   }
                                               }

                                               return new ModelFgaIncrementalChange {
                                                   DiagnosticLocation =
                                                       Location.Create(f.Path, new TextSpan(), new LinePositionSpan()),
                                                   AuthorizationModel = authModel,
                                               };
                                           }
                                       )
                                      .Collect();

        // Filters for classes with the OpenFgaTypeIdAttribute
        var fgaTypeIdProvider = context.SyntaxProvider.ForAttributeWithMetadataName(
            typeof(SealedFgaTypeIdAttribute).FullName!,
            static (synNode, _) => synNode is ClassDeclarationSyntax,
            static (context, _) => {
                var classDeclaration = (ClassDeclarationSyntax) context.TargetNode;
                var attribute = context.Attributes[0];
                return IdClassToGenerateData.From(
                    attribute,
                    classDeclaration,
                    context.TargetSymbol
                );
            }
        ).Collect();

        // Combine both so that we are triggered when either the model.fga file changes or a class with the OpenFgaTypeIdAttribute is added
        var fgaRelatedChangesProvider = modelFgaProvider.Combine(fgaTypeIdProvider);

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
        (ImmutableArray<ModelFgaIncrementalChange> modelFiles, ImmutableArray<IdClassToGenerateData> idClasses)
            fgaRelatedChanges
    ) {
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
                        modelFile.DiagnosticLocation
                    )
                );
            }
            return;
        }

        var modelFileChange = modelFiles[0];
        if (modelFileChange.AuthorizationModel is null) {
            context.ReportDiagnostic(Diagnostic.Create(
                    InvalidModelFgaFileRule,
                    modelFileChange.DiagnosticLocation
                )
            );
            return;
        }

        foreach (var idClassToGenerate in fgaRelatedChanges.idClasses) {
            AddGeneratedFilesForFgaType(context,
                modelFileChange.AuthorizationModel,
                idClassToGenerate
            );
        }

        var generatedSealedFgaFile = SealedFgaInitGenerator.Generate(fgaRelatedChanges.idClasses);
        context.AddSource(generatedSealedFgaFile.FileName, generatedSealedFgaFile.BuildFullFileContent());
    }

    private static void AddGeneratedFilesForFgaType(
        SourceProductionContext context,
        AuthorizationModel authModel,
        IdClassToGenerateData idClassToGenerate
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

        // Generate the partial class for the OpenFGA ID type
        var generatedIdFile = TypeNameIdGenerator.Generate(idClassToGenerate);
        context.AddSource(generatedIdFile.FileName, generatedIdFile.BuildFullFileContent());

        // Generate the relation types for the OpenFGA ID type
        var generatedRelationFiles = TypeNameRelationsGenerator.Generate(authModel, idClassToGenerate);
        foreach (var generatedRelationsFile in generatedRelationFiles) {
            context.AddSource(generatedRelationsFile.FileName, generatedRelationsFile.BuildFullFileContent());
        }
    }
}
