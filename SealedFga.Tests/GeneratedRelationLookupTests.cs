using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>
///     Runs the members the generator emits onto relation classes (<c>AllRelations</c>,
///     <c>TryFromOpenFgaString</c>), which the snapshots only show as text: the generated code is compiled
///     and loaded so the lookups return the actual constants.
/// </summary>
[Collection(GlobalCollection.Name)]
public sealed class GeneratedRelationLookupTests : IDisposable {
    private const string Model =
        """
        model
          schema 1.1
        type user
        type secret
          relations
            define OwnedBy: [user]
            define can_view: [user]
            define can_edit: [user]
        """;

    private const string Source =
        """
        using SealedFga.Attributes;
        using SealedFga.Models;
        namespace TestApp;
        [SealedFgaTypeId("secret", SealedFgaTypeIdType.Guid)]
        public readonly partial record struct SecretEntityId;
        """;

    private readonly AssemblyLoadContext _context = new(nameof(GeneratedRelationLookupTests), isCollectible: true);
    private readonly Type _permissions;

    public GeneratedRelationLookupTests() {
        // Only the ID and its relation classes: the DI wiring files need host assemblies this test domain lacks.
        var generated = GeneratorTestHarness.Run(Source, Model).GeneratedTrees
                                            .Where(t => Path.GetFileName(t.FilePath).StartsWith("SecretEntityId"));
        var compilation = GeneratorTestHarness.CreateCompilation(Source).AddSyntaxTrees(generated);
        using var image = new MemoryStream();
        var emit = compilation.Emit(image);
        emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        image.Position = 0;
        _permissions = _context.LoadFromStream(image).GetType("TestApp.SecretEntityIdPermissions", throwOnError: true)!;
    }

    public void Dispose() => _context.Unload();

    [Fact]
    public void AllRelations_lists_the_class_constants_in_model_order() {
        var all = ((IEnumerable) Field("AllRelations")).Cast<object>().ToList();

        all.Select(r => r.ToString()).ShouldBe(["can_view", "can_edit"]);
        all[0].ShouldBeSameAs(Field("can_view"));
        all[1].ShouldBeSameAs(Field("can_edit"));
    }

    [Fact]
    public void TryFromOpenFgaString_returns_the_generated_constant() {
        var (found, relation) = TryFrom("can_edit");

        found.ShouldBeTrue();
        relation.ShouldBeSameAs(Field("can_edit"));
    }

    [Theory]
    [InlineData("can_delete")]
    [InlineData("CAN_VIEW")]
    [InlineData("OwnedBy")] // a group relation, which lives in SecretEntityIdGroups
    [InlineData("")]
    [InlineData(null)]
    public void TryFromOpenFgaString_rejects_names_the_class_does_not_have(string? name) {
        var (found, relation) = TryFrom(name);

        found.ShouldBeFalse();
        relation.ShouldBeNull();
    }

    [Fact]
    public void FromOpenFgaString_stays_lenient_and_equals_the_constant_by_value() {
        var fromOpenFgaString = _permissions.GetMethod("FromOpenFgaString")!;

        fromOpenFgaString.Invoke(null, ["anything"])!.ToString().ShouldBe("anything");
        fromOpenFgaString.Invoke(null, ["can_view"]).ShouldBe(Field("can_view"));
    }

    private object Field(string name) =>
        _permissions.GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    private (bool Found, object? Relation) TryFrom(string? name) {
        object?[] args = [name, null];
        var found = (bool) _permissions.GetMethod("TryFromOpenFgaString")!.Invoke(null, args)!;
        return (found, args[1]);
    }
}
