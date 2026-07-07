using SealedFga.Util;
using VerifyTests;
using Xunit;

namespace SealedFga.Tests.Support;

/// <summary>
///     Assembly-wide one-time setup, shared by every test class via <see cref="GlobalCollection" />.
///     <see cref="IdUtil" /> is static global mutable state, so the test ID types are registered exactly
///     once here (mimicking the generated <c>SealedFgaInit.Initialize</c>). Also initializes Verify for
///     the source-generator snapshot tests.
///     <para>
///         A collection fixture (rather than a <c>[ModuleInitializer]</c>) is used deliberately: the
///         library polyfills <c>ModuleInitializerAttribute</c> via PolySharp, and with
///         <c>InternalsVisibleTo</c> naming that attribute in test code would collide with the BCL type.
///     </para>
/// </summary>
public sealed class GlobalFixture {
    public GlobalFixture() {
        IdUtil.RegisterIdType(typeof(TestObjectId), TestObjectId.OpenFgaTypeName);
        IdUtil.RegisterIdType(typeof(TestParentId), TestParentId.OpenFgaTypeName);
        IdUtil.RegisterIdType(typeof(TestUserId), TestUserId.OpenFgaTypeName);

        IdUtil.RegisterIdTypeParseMethod(typeof(TestObjectId), s => TestObjectId.Parse(s));
        IdUtil.RegisterIdTypeParseMethod(typeof(TestParentId), s => TestParentId.Parse(s));
        IdUtil.RegisterIdTypeParseMethod(typeof(TestUserId), s => TestUserId.Parse(s));

        VerifySourceGenerators.Initialize();
    }
}

/// <summary>The single collection that all test classes join so they share one <see cref="GlobalFixture" />.</summary>
[CollectionDefinition(Name)]
public sealed class GlobalCollection : ICollectionFixture<GlobalFixture> {
    public const string Name = "sealedfga";
}
