using System.Threading.Tasks;
using SealedFga.Tests.Support;
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

    // NOTE: There is intentionally no SFGA001 (malformed model.fga) test. The DSL parser never returns a
    // graceful failure for the inputs tried — a referenced type that is simply absent surfaces as SFGA002
    // (covered above), while genuinely un-parseable input throws inside the parser and is surfaced by
    // Roslyn as a CS8785 generator-exception *warning* rather than SFGA001. That robustness gap is a
    // library finding (the generator should catch parser exceptions and emit SFGA001), not something to
    // pin as expected behaviour here.
}
