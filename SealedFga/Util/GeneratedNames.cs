using SealedFga.AuthModel;

namespace SealedFga.Util;

/// <summary>
///     Names of members the source generator emits onto ID classes, for reflection-based access.
///     The authoritative constants live in the generator
///     (<c>SealedFga.Analyzers/Generators/AuthModel/$TypeName$Id.Generator.cs</c>,
///     <c>TypeNameIdGenerator</c>); the runtime cannot reference the generator assembly, so the
///     names are mirrored here — keep both in sync. The names are also pinned by the generator
///     snapshot tests and the hand-written ID mirrors in <c>SealedFga.Tests</c>.
/// </summary>
internal static class GeneratedNames {
    /// <summary>Instance method returning the ID in OpenFGA tuple string form (declared on <see cref="ISealedFgaUser" />).</summary>
    public const string OpenFgaIdTupleStringMethodName = nameof(ISealedFgaUser.AsOpenFgaIdTupleString);

    /// <summary>Static property on generated ID classes holding the OpenFGA type name.</summary>
    public const string OpenFgaTypeNamePropertyName = "OpenFgaTypeName";
}
