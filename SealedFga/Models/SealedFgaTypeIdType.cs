namespace SealedFga.Models;

/// <summary>
///     Backing-value kind of a <c>[SealedFgaTypeId]</c> ID class. The generator keeps an internal
///     copy of this enum (<c>SealedFga.Analyzers/Models/SealedFgaTypeIdType.cs</c>) because it
///     cannot reference this assembly; the member order is part of the attribute's binary
///     contract — keep both files in sync.
/// </summary>
public enum SealedFgaTypeIdType {
    String,
    Guid,

    // New members are appended: the ordinal value is the attribute's binary contract (the
    // constructor argument is stored/unboxed by value). Never reorder or insert.
    Int,
    Long,
}
