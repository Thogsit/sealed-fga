using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>
///     Source-generator snapshot tests. Each runs <see cref="SealedFgaSourceGenerator" /> and captures
///     the full set of emitted files plus any diagnostics via Verify.
/// </summary>
[Collection(GlobalCollection.Name)] // ensures VerifySourceGenerators.Initialize() has run
public class GeneratorTests {
    // A model with both a lowercase relation (can_view -> *Permissions) and an uppercase relation
    // (OwnedBy -> *Groups), so the casing split is exercised.
    private const string SecretModel =
        """
        model
          schema 1.1
        type user
        type agency
          relations
            define Member: [user]
        type secret
          relations
            define OwnedBy: [agency]
            define can_view: [user] or Member from OwnedBy
        """;

    [Fact]
    public Task Guid_backed_id_with_mixed_relation_casing() {
        const string source =
            """
            using SealedFga.Attributes;
            using SealedFga.Models;
            namespace TestApp;
            [SealedFgaTypeId("secret", SealedFgaTypeIdType.Guid)]
            public readonly partial record struct SecretEntityId;
            """;
        return GeneratorTestHarness.Verify(source, SecretModel);
    }

    [Fact]
    public Task String_backed_id_with_uppercase_only_relations() {
        const string source =
            """
            using SealedFga.Attributes;
            using SealedFga.Models;
            namespace TestApp;
            [SealedFgaTypeId("agency", SealedFgaTypeIdType.String)]
            public readonly partial record struct AgencyEntityId;
            """;
        // agency has only the uppercase relation Member -> only a *Groups file should be emitted.
        return GeneratorTestHarness.Verify(source, SecretModel);
    }

    [Fact]
    public Task Split_toggle_off_emits_single_relations_class() {
        const string source =
            """
            using SealedFga.Attributes;
            using SealedFga.Models;
            namespace TestApp;
            [SealedFgaTypeId("secret", SealedFgaTypeIdType.Guid)]
            public readonly partial record struct SecretEntityId;
            """;
        // SealedFgaSplitRelationClasses=false -> both can_view (lowercase) and OwnedBy (uppercase)
        // land in a single SecretEntityIdRelations class; no *Permissions/*Groups files.
        return GeneratorTestHarness.Verify(
            source,
            SecretModel,
            new Dictionary<string, string> { ["build_property.SealedFgaSplitRelationClasses"] = "false" }
        );
    }

    [Fact]
    public void Split_toggle_with_unparseable_value_keeps_default_split() {
        const string source =
            """
            using SealedFga.Attributes;
            using SealedFga.Models;
            namespace TestApp;
            [SealedFgaTypeId("secret", SealedFgaTypeIdType.Guid)]
            public readonly partial record struct SecretEntityId;
            """;
        var result = GeneratorTestHarness.Run(
            source,
            SecretModel,
            new Dictionary<string, string> { ["build_property.SealedFgaSplitRelationClasses"] = "banana" }
        );

        var hintNames = result.Results.Single().GeneratedSources.Select(s => s.HintName).ToList();
        hintNames.ShouldContain("SecretEntityIdPermissions.g.cs");
        hintNames.ShouldContain("SecretEntityIdGroups.g.cs");
        hintNames.ShouldNotContain("SecretEntityIdRelations.g.cs");
    }

    [Fact]
    public Task Int_backed_id_omits_new_and_uses_numeric_converters() {
        // release-channel has an int-backed ID (SCBackend's real case). The generated struct must
        // omit New() (no sensible generated value for an identity column) and wire the Int32
        // TypeConverter/JSON/EF converters.
        const string source =
            """
            using SealedFga.Attributes;
            using SealedFga.Models;
            namespace TestApp;
            [SealedFgaTypeId("secret", SealedFgaTypeIdType.Int)]
            public readonly partial record struct SecretEntityId;
            """;
        return GeneratorTestHarness.Verify(source, SecretModel);
    }

    [Fact]
    public Task Long_backed_id_omits_new_and_uses_numeric_converters() {
        const string source =
            """
            using SealedFga.Attributes;
            using SealedFga.Models;
            namespace TestApp;
            [SealedFgaTypeId("secret", SealedFgaTypeIdType.Long)]
            public readonly partial record struct SecretEntityId;
            """;
        return GeneratorTestHarness.Verify(source, SecretModel);
    }

    [Fact]
    public Task Multiple_id_classes_register_in_init() {
        const string source =
            """
            using SealedFga.Attributes;
            using SealedFga.Models;
            namespace TestApp;
            [SealedFgaTypeId("secret", SealedFgaTypeIdType.Guid)]
            public readonly partial record struct SecretEntityId;
            [SealedFgaTypeId("user", SealedFgaTypeIdType.String)]
            public readonly partial record struct UserEntityId;
            """;
        return GeneratorTestHarness.Verify(source, SecretModel);
    }

    [Fact]
    public Task Check_dispatch_covers_each_checkable_type() {
        // secret has both can_view (Permissions) and OwnedBy (Groups) -> dispatches via *Permissions;
        // agency has only Member (Groups) -> dispatches via *Groups; user has no relations -> omitted.
        const string source =
            """
            using SealedFga.Attributes;
            using SealedFga.Models;
            namespace TestApp;
            [SealedFgaTypeId("secret", SealedFgaTypeIdType.Guid)]
            public readonly partial record struct SecretEntityId;
            [SealedFgaTypeId("agency", SealedFgaTypeIdType.String)]
            public readonly partial record struct AgencyEntityId;
            [SealedFgaTypeId("user", SealedFgaTypeIdType.String)]
            public readonly partial record struct UserEntityId;
            """;
        return GeneratorTestHarness.Verify(source, SecretModel);
    }

    [Fact]
    public void Check_dispatch_picks_the_emitted_relation_class_and_omits_relationless_types() {
        const string source =
            """
            using SealedFga.Attributes;
            using SealedFga.Models;
            namespace TestApp;
            [SealedFgaTypeId("secret", SealedFgaTypeIdType.Guid)]
            public readonly partial record struct SecretEntityId;
            [SealedFgaTypeId("agency", SealedFgaTypeIdType.String)]
            public readonly partial record struct AgencyEntityId;
            [SealedFgaTypeId("user", SealedFgaTypeIdType.String)]
            public readonly partial record struct UserEntityId;
            """;
        var result = GeneratorTestHarness.Run(source, SecretModel);

        var dispatch = result.Results.Single().GeneratedSources
                             .Single(s => s.HintName == "SealedFgaCheckDispatch.g.cs")
                             .SourceText.ToString();

        // secret has a can_* permission -> *Permissions is the emitted class the dispatcher must use.
        dispatch.ShouldContain(
            "\"secret\" => await svc.CheckAsync(user, TestApp.SecretEntityIdPermissions.FromOpenFgaString(relation), TestApp.SecretEntityId.Parse(objectId), queryOptions, cancellationToken),"
        );
        // agency has only an uppercase relation -> only *Groups is emitted, so the dispatcher must use it.
        dispatch.ShouldContain(
            "\"agency\" => await svc.CheckAsync(user, TestApp.AgencyEntityIdGroups.FromOpenFgaString(relation), TestApp.AgencyEntityId.Parse(objectId), queryOptions, cancellationToken),"
        );
        // user has no relations -> not checkable -> no arm (falls through to the null default).
        dispatch.ShouldNotContain("\"user\" =>");
    }

    [Fact]
    public void Check_dispatch_uses_single_relations_class_when_split_disabled() {
        const string source =
            """
            using SealedFga.Attributes;
            using SealedFga.Models;
            namespace TestApp;
            [SealedFgaTypeId("secret", SealedFgaTypeIdType.Guid)]
            public readonly partial record struct SecretEntityId;
            """;
        var result = GeneratorTestHarness.Run(
            source,
            SecretModel,
            new Dictionary<string, string> { ["build_property.SealedFgaSplitRelationClasses"] = "false" }
        );

        var dispatch = result.Results.Single().GeneratedSources
                             .Single(s => s.HintName == "SealedFgaCheckDispatch.g.cs")
                             .SourceText.ToString();

        // With the split off there is one SecretEntityIdRelations class, so the dispatcher must use it.
        dispatch.ShouldContain("TestApp.SecretEntityIdRelations.FromOpenFgaString(relation)");
        dispatch.ShouldNotContain("SecretEntityIdPermissions");
    }

    [Fact]
    public Task Entity_includes_lists_only_navigation_properties() {
        // SecretEntity mixes a reference nav (OwningAgency), a collection nav (RelatedAgencies), an FK id
        // property (OwningAgencyId), a primitive (Value) and its own Id. Only the two navigations should
        // appear in SecretEntityIncludes; AgencyEntity has no navigations, so it emits no includes file.
        const string source =
            """
            using System.Collections.Generic;
            using SealedFga.Attributes;
            using SealedFga.AuthModel;
            using SealedFga.Models;
            namespace TestApp;

            [SealedFgaTypeId("secret", SealedFgaTypeIdType.Guid)]
            public readonly partial record struct SecretEntityId;

            [SealedFgaTypeId("agency", SealedFgaTypeIdType.Guid)]
            public readonly partial record struct AgencyEntityId;

            public class AgencyEntity : ISealedFgaType<AgencyEntityId> {
                public AgencyEntityId Id { get; set; }
                public string Name { get; set; }
            }

            public class SecretEntity : ISealedFgaType<SecretEntityId> {
                public SecretEntityId Id { get; set; }
                public AgencyEntityId OwningAgencyId { get; set; }
                public AgencyEntity OwningAgency { get; set; }
                public List<AgencyEntity> RelatedAgencies { get; set; }
                public string Value { get; set; }
            }
            """;
        return GeneratorTestHarness.Verify(source, SecretModel);
    }

    [Fact]
    public Task Tuple_source_entity_generates_like_any_other() {
        // An ISealedFgaTupleSource entity feeds the runtime interceptor only — the generator must
        // treat it exactly like any other ISealedFgaType entity (id partial, relations, init,
        // dispatch, interceptor; no extra or missing files) and report no diagnostics.
        const string source =
            """
            using System.Collections.Generic;
            using SealedFga.Attributes;
            using SealedFga.AuthModel;
            using SealedFga.Fga;
            using SealedFga.Models;
            namespace TestApp;

            [SealedFgaTypeId("secret", SealedFgaTypeIdType.Guid)]
            public readonly partial record struct SecretEntityId;

            public class SecretGrantEntity : ISealedFgaType<SecretEntityId>, ISealedFgaTupleSource {
                public SecretEntityId Id { get; set; }
                public bool Active { get; set; }
                public IEnumerable<SealedFgaTupleOperation> DesiredTuples() => [];
            }
            """;
        return GeneratorTestHarness.Verify(source, SecretModel);
    }

    [Fact]
    public Task Entity_without_navigations_emits_no_includes_file() {
        // UserEntity has only its Id and an FK id property, both of which are relationship keys rather than
        // navigations, so no UserEntityIncludes file should be emitted.
        const string source =
            """
            using SealedFga.Attributes;
            using SealedFga.AuthModel;
            using SealedFga.Models;
            namespace TestApp;

            [SealedFgaTypeId("user", SealedFgaTypeIdType.String)]
            public readonly partial record struct UserEntityId;

            [SealedFgaTypeId("agency", SealedFgaTypeIdType.Guid)]
            public readonly partial record struct AgencyEntityId;

            public class UserEntity : ISealedFgaType<UserEntityId> {
                public UserEntityId Id { get; set; }
                public AgencyEntityId AgencyId { get; set; }
            }
            """;
        return GeneratorTestHarness.Verify(source, SecretModel);
    }

    [Fact]
    public Task Unknown_open_fga_type_reports_SFGA002() {
        const string source =
            """
            using SealedFga.Attributes;
            using SealedFga.Models;
            namespace TestApp;
            [SealedFgaTypeId("does_not_exist", SealedFgaTypeIdType.Guid)]
            public readonly partial record struct GhostEntityId;
            """;
        return GeneratorTestHarness.Verify(source, SecretModel);
    }

    [Fact]
    public void Non_default_named_fga_file_is_picked_up() {
        // The package's build props auto-include *.fga (any name), so the generator must accept a model
        // file that is not literally called "model.fga".
        const string source =
            """
            using SealedFga.Attributes;
            using SealedFga.Models;
            namespace TestApp;
            [SealedFgaTypeId("secret", SealedFgaTypeIdType.Guid)]
            public readonly partial record struct SecretEntityId;
            """;
        var result = GeneratorTestHarness.RunWithModelFiles(source, ("authz.fga", SecretModel));

        result.Diagnostics.ShouldBeEmpty();
        result.GeneratedTrees
              .ShouldContain(t => t.FilePath.EndsWith("SealedFgaInit.g.cs"));
    }

    [Fact]
    public void Multiple_fga_files_report_SFGA003() {
        const string source =
            """
            using SealedFga.Attributes;
            using SealedFga.Models;
            namespace TestApp;
            [SealedFgaTypeId("secret", SealedFgaTypeIdType.Guid)]
            public readonly partial record struct SecretEntityId;
            """;
        var result = GeneratorTestHarness.RunWithModelFiles(
            source,
            ("model.fga", SecretModel),
            ("other.fga", SecretModel)
        );

        result.Diagnostics.Select(d => d.Id).ShouldContain("SFGA003");
    }

    [Fact]
    public void Malformed_model_reports_SFGA001() {
        const string source =
            """
            namespace TestApp;
            public class Unrelated;
            """;
        var result = GeneratorTestHarness.RunWithModelFiles(source, ("model.fga", "this is not a model"));

        // The generator must catch parser failures itself — a crash would surface as a CS8785
        // generator-exception warning instead of SFGA001.
        result.Results.Single().Exception.ShouldBeNull();
        result.Diagnostics.ShouldContain(d => d.Id == "SFGA001");
    }

    [Fact]
    public void SFGA001_points_at_the_offending_line() {
        const string source =
            """
            namespace TestApp;
            public class Unrelated;
            """;
        // Line 4 (0-based: 3) is garbage inside an otherwise valid model.
        const string badModel =
            """
            model
              schema 1.1
            type user
            garbage here
            """;
        var result = GeneratorTestHarness.RunWithModelFiles(source, ("model.fga", badModel));

        result.Results.Single().Exception.ShouldBeNull();
        var diagnostics = result.Diagnostics.Where(d => d.Id == "SFGA001").ToList();
        diagnostics.ShouldNotBeEmpty();
        diagnostics.ShouldContain(d => d.Location.GetLineSpan().StartLinePosition.Line == 3);
    }
}
