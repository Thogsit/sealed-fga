namespace SealedFga.Models;

/// <summary>
///     Generator-side copy of the runtime library's <c>SealedFga.Models.SealedFgaTypeIdType</c>
///     (<c>SealedFga/Models/SealedFgaTypeIdType.cs</c>). The generator (netstandard2.0) cannot
///     reference the net10.0 runtime assembly, and a <em>public</em> duplicate would be ambiguous
///     (CS0433) for anything referencing both assemblies, so this copy is internal. The member
///     order is part of the <c>[SealedFgaTypeId]</c> attribute's binary contract
///     (<c>IdClassToGenerateData</c> unboxes the constructor argument by value) — keep both files
///     in sync.
/// </summary>
internal enum SealedFgaTypeIdType {
    String,
    Guid,

    // New members are appended: the ordinal value is the attribute's binary contract (the
    // constructor argument is stored/unboxed by value). Never reorder or insert. Keep in sync
    // with the runtime copy (SealedFga/Models/SealedFgaTypeIdType.cs).
    Int,
    Long,
}
