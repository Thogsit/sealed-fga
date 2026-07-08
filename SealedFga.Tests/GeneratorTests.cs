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
    // A model with both a lowercase relation (can_view -> *Attributes) and an uppercase relation
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
            public partial class SecretEntityId;
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
            public partial class AgencyEntityId;
            """;
        // agency has only the uppercase relation Member -> only a *Groups file should be emitted.
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
            public partial class SecretEntityId;
            [SealedFgaTypeId("user", SealedFgaTypeIdType.String)]
            public partial class UserEntityId;
            """;
        return GeneratorTestHarness.Verify(source, SecretModel);
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
            public partial class SecretEntityId;

            [SealedFgaTypeId("agency", SealedFgaTypeIdType.Guid)]
            public partial class AgencyEntityId;

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
            public partial class UserEntityId;

            [SealedFgaTypeId("agency", SealedFgaTypeIdType.Guid)]
            public partial class AgencyEntityId;

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
            public partial class GhostEntityId;
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
            public partial class SecretEntityId;
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
            public partial class SecretEntityId;
            """;
        var result = GeneratorTestHarness.RunWithModelFiles(
            source,
            ("model.fga", SecretModel),
            ("other.fga", SecretModel)
        );

        result.Diagnostics.Select(d => d.Id).ShouldContain("SFGA003");
    }

    // NOTE: There is intentionally no SFGA001 (malformed model.fga) test. The DSL parser never returns a
    // graceful failure for the inputs tried — a referenced type that is simply absent surfaces as SFGA002
    // (covered above), while genuinely un-parseable input throws inside the parser and is surfaced by
    // Roslyn as a CS8785 generator-exception *warning* rather than SFGA001. That robustness gap is a
    // library finding (the generator should catch parser exceptions and emit SFGA001), not something to
    // pin as expected behaviour here.
}
