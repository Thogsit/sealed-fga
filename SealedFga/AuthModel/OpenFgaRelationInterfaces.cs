using System;

namespace SealedFga.AuthModel;

/// <summary>
///     Used to strongly type enums for representation of OpenFGA relations.
///     <para>
///         Equality is by value: two relations are equal when they are of the same concrete class and wrap the same
///         relation string, so an instance from <c>FromOpenFgaString</c> equals the generated constant and both
///         work as set or dictionary keys. Compare through <see cref="Equals(SealedFgaRelation?)" /> or the class
///         type, not through an <see cref="ISealedFgaRelation{TObjId}" />-typed <c>==</c>, which C# resolves to
///         reference equality.
///     </para>
/// </summary>
/// <param name="val">The enum's value, i.e. the OpenFGA relation string, e.g. <c>"can_view"</c>.</param>
public abstract class SealedFgaRelation(string val) : IEquatable<SealedFgaRelation> {
    /// <summary>
    ///     The raw relation name. Init-only because it feeds <see cref="GetHashCode" />: mutating it would lose the
    ///     instance inside any set or dictionary holding it.
    /// </summary>
    public string Value { get; init; } = val;

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>
    ///     Returns the relation in the OpenFGA string representation.
    /// </summary>
    /// <returns>The OpenFGA relation string, e.g. <c>"can_view"</c></returns>
    public string AsOpenFgaString() => Value;

    /// <summary>Same concrete relation class and the same relation string (ordinal).</summary>
    /// <param name="other">The relation to compare with.</param>
    public bool Equals(SealedFgaRelation? other)
        => other is not null
           && (ReferenceEquals(this, other)
               || (GetType() == other.GetType() && string.Equals(Value, other.Value, StringComparison.Ordinal)));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as SealedFgaRelation);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(GetType(), StringComparer.Ordinal.GetHashCode(Value));

    /// <summary>Value equality, see <see cref="Equals(SealedFgaRelation?)" />.</summary>
    public static bool operator ==(SealedFgaRelation? left, SealedFgaRelation? right)
        => left?.Equals(right) ?? right is null;

    /// <summary>Value inequality, see <see cref="Equals(SealedFgaRelation?)" />.</summary>
    public static bool operator !=(SealedFgaRelation? left, SealedFgaRelation? right) => !(left == right);
}

/// <summary>
///     Used to strongly type enums for representation of OpenFGA relations.
/// </summary>
/// <typeparam name="TObjId">The related object entity ID's type.</typeparam>
public interface ISealedFgaRelation<TObjId> : ISealedFgaRelationWithoutAssociatedType
    where TObjId : ISealedFgaTypeIdWithoutAssociatedIdType {
    /// <summary>
    ///     Returns the relation in the OpenFGA string representation.
    /// </summary>
    /// <returns>The OpenFGA relation string, e.g. <c>"can_view"</c></returns>
    public string AsOpenFgaString();
}

public interface ISealedFgaRelationWithoutAssociatedType;
