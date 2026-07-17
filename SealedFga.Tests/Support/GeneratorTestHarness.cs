using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using SealedFga.Attributes;
using VerifyTests;
using VerifyXunit;

namespace SealedFga.Tests.Support;

/// <summary>
///     Runs <see cref="SealedFgaSourceGenerator" /> over an in-memory compilation plus a
///     <c>model.fga</c> additional file, and snapshots the result with Verify.
/// </summary>
public static class GeneratorTestHarness {
    /// <summary>An in-memory <c>*.fga</c> file so the generator's AdditionalTexts input fires.</summary>
    private sealed class ModelFgaText(string content, string path = "model.fga") : AdditionalText {
        public override string Path { get; } = path;
        public override SourceText GetText(CancellationToken cancellationToken = default)
            => SourceText.From(content, Encoding.UTF8);
    }

    /// <summary>
    ///     Simulates MSBuild <c>CompilerVisibleProperty</c> values (keys must include the
    ///     <c>build_property.</c> prefix) for generator options like <c>SealedFgaSplitRelationClasses</c>.
    /// </summary>
    private sealed class TestAnalyzerConfigOptionsProvider(IReadOnlyDictionary<string, string> globalOptions)
        : AnalyzerConfigOptionsProvider {
        private sealed class DictionaryOptions(IReadOnlyDictionary<string, string> options) : AnalyzerConfigOptions {
            public override bool TryGetValue(string key, out string value)
                => options.TryGetValue(key, out value!);
        }

        public override AnalyzerConfigOptions GlobalOptions { get; } = new DictionaryOptions(globalOptions);
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;
    }

    private static readonly IReadOnlyList<MetadataReference> References =
        AppDomain.CurrentDomain.GetAssemblies()
                 .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                 .Select(a => (MetadataReference) MetadataReference.CreateFromFile(a.Location))
                 // Guarantee the SealedFga runtime assembly (attributes/enums the generator reads) is present.
                 .Append(MetadataReference.CreateFromFile(typeof(SealedFgaTypeIdAttribute).Assembly.Location))
                 .Distinct()
                 .ToList();

    private static GeneratorDriver BuildDriver(
        string source,
        IReadOnlyDictionary<string, string>? buildProperties,
        params (string path, string content)[] models
    ) {
        var compilation = CSharpCompilation.Create(
            "SealedFga.GeneratorInput",
            [CSharpSyntaxTree.ParseText(source)],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        return CSharpGeneratorDriver.Create(
            [new SealedFgaSourceGenerator().AsSourceGenerator()],
            models.Select(m => (AdditionalText) new ModelFgaText(m.content, m.path)).ToArray(),
            optionsProvider: buildProperties is null
                ? null
                : new TestAnalyzerConfigOptionsProvider(buildProperties)
        ).RunGenerators(compilation);
    }

    /// <summary>Runs the generator and returns the run result (for direct assertions on diagnostics).</summary>
    public static GeneratorDriverRunResult Run(
        string source,
        string modelFga,
        IReadOnlyDictionary<string, string>? buildProperties = null
    )
        => BuildDriver(source, buildProperties, ("model.fga", modelFga)).GetRunResult();

    /// <summary>
    ///     Runs the generator over an arbitrary set of <c>*.fga</c> files (custom names / multiple files)
    ///     and returns the run result for direct assertions on diagnostics and emitted sources.
    /// </summary>
    public static GeneratorDriverRunResult RunWithModelFiles(string source, params (string path, string content)[] models)
        => BuildDriver(source, null, models).GetRunResult();

    /// <summary>Runs the generator and snapshots the emitted files + diagnostics with Verify.</summary>
    public static Task Verify(
        string source,
        string modelFga,
        IReadOnlyDictionary<string, string>? buildProperties = null
    ) {
        var settings = new VerifySettings();
        settings.UseDirectory("Snapshots");
        // The generator emits usings from an unordered HashSet, so sort them for deterministic snapshots.
        settings.AddScrubber(SortUsingBlocks);
        return Verifier.Verify(BuildDriver(source, buildProperties, ("model.fga", modelFga)), settings);
    }

    /// <summary>Sorts each maximal run of consecutive <c>using ...;</c> lines in the snapshot text.</summary>
    private static void SortUsingBlocks(StringBuilder builder) {
        var lines = builder.ToString().Replace("\r\n", "\n").Split('\n').ToList();
        var i = 0;
        while (i < lines.Count) {
            if (!IsUsing(lines[i])) {
                i++;
                continue;
            }

            var start = i;
            while (i < lines.Count && IsUsing(lines[i])) {
                i++;
            }

            var block = lines.GetRange(start, i - start);
            block.Sort(StringComparer.Ordinal);
            for (var j = 0; j < block.Count; j++) {
                lines[start + j] = block[j];
            }
        }

        builder.Clear();
        builder.Append(string.Join("\n", lines));
        return;

        static bool IsUsing(string line) => line.StartsWith("using ", StringComparison.Ordinal) && line.TrimEnd().EndsWith(";");
    }
}
