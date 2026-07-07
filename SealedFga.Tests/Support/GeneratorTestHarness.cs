using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
    /// <summary>An in-memory <c>model.fga</c> so the generator's AdditionalTexts input fires.</summary>
    private sealed class ModelFgaText(string content) : AdditionalText {
        public override string Path => "model.fga";
        public override SourceText GetText(CancellationToken cancellationToken = default)
            => SourceText.From(content, Encoding.UTF8);
    }

    private static readonly IReadOnlyList<MetadataReference> References =
        AppDomain.CurrentDomain.GetAssemblies()
                 .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                 .Select(a => (MetadataReference) MetadataReference.CreateFromFile(a.Location))
                 // Guarantee the SealedFga runtime assembly (attributes/enums the generator reads) is present.
                 .Append(MetadataReference.CreateFromFile(typeof(SealedFgaTypeIdAttribute).Assembly.Location))
                 .Distinct()
                 .ToList();

    private static GeneratorDriver BuildDriver(string source, string modelFga) {
        var compilation = CSharpCompilation.Create(
            "SealedFga.GeneratorInput",
            [CSharpSyntaxTree.ParseText(source)],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        return CSharpGeneratorDriver.Create(
            [new SealedFgaSourceGenerator().AsSourceGenerator()],
            [new ModelFgaText(modelFga)]
        ).RunGenerators(compilation);
    }

    /// <summary>Runs the generator and returns the run result (for direct assertions on diagnostics).</summary>
    public static GeneratorDriverRunResult Run(string source, string modelFga)
        => BuildDriver(source, modelFga).GetRunResult();

    /// <summary>Runs the generator and snapshots the emitted files + diagnostics with Verify.</summary>
    public static Task Verify(string source, string modelFga) {
        var settings = new VerifySettings();
        settings.UseDirectory("Snapshots");
        // The generator emits usings from an unordered HashSet, so sort them for deterministic snapshots.
        settings.AddScrubber(SortUsingBlocks);
        return Verifier.Verify(BuildDriver(source, modelFga), settings);
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
