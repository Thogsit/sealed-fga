using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>
///     Tests for <see cref="SealedFgaTupleSourceAnalyzer" /> (SFGA004): an entity implementing
///     <c>ISealedFgaTupleSource</c> must not also declare <c>[SealedFgaRelation]</c> /
///     <c>[SealedFgaJoinRelation]</c> — the tuple source owns all of the entity's tuples.
/// </summary>
public class TupleSourceAnalyzerTests {
    private const string EntityPreamble =
        """
        using System;
        using System.Collections.Generic;
        using SealedFga.Attributes;
        using SealedFga.AuthModel;
        using SealedFga.Fga;
        namespace TestApp;

        public readonly record struct ObjId(Guid Value) : ISealedFgaTypeId<ObjId> {
            public static string OpenFgaTypeName => "obj";
            public static ObjId New() => new(Guid.NewGuid());
            public static ObjId Parse(string val) => new(Guid.Parse(val));
            public string AsOpenFgaIdTupleString() => $"{OpenFgaTypeName}:{Value}";
        }

        public readonly record struct ParentId(Guid Value) : ISealedFgaTypeId<ParentId> {
            public static string OpenFgaTypeName => "parent";
            public static ParentId New() => new(Guid.NewGuid());
            public static ParentId Parse(string val) => new(Guid.Parse(val));
            public string AsOpenFgaIdTupleString() => $"{OpenFgaTypeName}:{Value}";
        }

        """;

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source) {
        var compilation = GeneratorTestHarness.CreateCompilation(source);
        var withAnalyzers = compilation.WithAnalyzers([new SealedFgaTupleSourceAnalyzer()]);
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    [Fact]
    public async Task Pure_tuple_source_reports_nothing() {
        var diagnostics = await AnalyzeAsync(EntityPreamble +
            """
            public class GrantEntity : ISealedFgaType<ObjId>, ISealedFgaTupleSource {
                public ObjId Id { get; set; }
                public IEnumerable<SealedFgaTupleOperation> DesiredTuples() => [];
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task Tuple_source_with_scalar_relation_attribute_reports_SFGA004() {
        var diagnostics = await AnalyzeAsync(EntityPreamble +
            """
            public class GrantEntity : ISealedFgaType<ObjId>, ISealedFgaTupleSource {
                public ObjId Id { get; set; }
                [SealedFgaRelation("OwnedBy")]
                public ParentId? ParentId { get; set; }
                public IEnumerable<SealedFgaTupleOperation> DesiredTuples() => [];
            }
            """);

        var diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("SFGA004");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.GetMessage().ShouldContain("GrantEntity");
        diagnostic.GetMessage().ShouldContain("[SealedFgaRelation]");
    }

    [Fact]
    public async Task Tuple_source_with_join_relation_attribute_reports_SFGA004() {
        var diagnostics = await AnalyzeAsync(EntityPreamble +
            """
            [SealedFgaJoinRelation("member", nameof(UserSide), nameof(ObjectSide))]
            public class GrantEntity : ISealedFgaTupleSource {
                public Guid Id { get; set; }
                public ParentId? UserSide { get; set; }
                public ObjId? ObjectSide { get; set; }
                public IEnumerable<SealedFgaTupleOperation> DesiredTuples() => [];
            }
            """);

        var diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("SFGA004");
        diagnostic.GetMessage().ShouldContain("[SealedFgaJoinRelation]");
    }

    [Fact]
    public async Task Attributes_without_tuple_source_report_nothing() {
        var diagnostics = await AnalyzeAsync(EntityPreamble +
            """
            public class PlainEntity : ISealedFgaType<ObjId> {
                public ObjId Id { get; set; }
                [SealedFgaRelation("OwnedBy")]
                public ParentId? ParentId { get; set; }
            }
            """);

        diagnostics.ShouldBeEmpty();
    }
}
