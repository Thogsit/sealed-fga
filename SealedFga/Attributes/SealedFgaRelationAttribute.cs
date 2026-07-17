using System;

namespace SealedFga.Attributes;

/// <summary>Which side of the emitted tuple the <b>annotated entity's own Id</b> takes.</summary>
public enum SealedFgaRelationTargetType {
    /// <summary>The entity is the tuple's <c>user</c>; the FK value is the <c>object</c>.</summary>
    User,

    /// <summary>The entity is the tuple's <c>object</c>; the FK value is the <c>user</c>. The default.</summary>
    Object,
}

/// <summary>
///     Declares that a <b>scalar FK property</b> emits an OpenFGA tuple linking the FK value and the
///     entity's own <c>Id</c> (the entity must implement <c>ISealedFgaType&lt;TId&gt;</c>).
///     <see cref="TargetType" /> orients the tuple: with the default
///     <see cref="SealedFgaRelationTargetType.Object" /> the FK is the <c>user</c> and the entity the
///     <c>object</c>; <see cref="SealedFgaRelationTargetType.User" /> swaps them.
///     <para>
///         Sync semantics (via the SaveChanges interceptor, transactional outbox): adding the entity
///         writes the tuple (skipped while the FK is <c>null</c>); re-pointing the FK deletes the old
///         tuple and writes the new; deleting the entity purges every tuple referencing its Id.
///     </para>
///     <para>
///         For a many-to-many join entity whose tuple links its two FK ends (neither side being the
///         row's own Id), use the class-level <see cref="SealedFgaJoinRelationAttribute" /> instead.
///     </para>
/// </summary>
/// <param name="relation">The OpenFGA relation name, e.g. <c>nameof(SecretEntityIdGroups.OwnedBy)</c>.</param>
/// <param name="targetType">Which tuple side the entity's own Id takes (default: <c>Object</c>).</param>
[AttributeUsage(AttributeTargets.Property)]
public class SealedFgaRelationAttribute(
    string relation,
    SealedFgaRelationTargetType targetType = SealedFgaRelationTargetType.Object) : Attribute {
    /// <summary>The OpenFGA relation name of the emitted tuple.</summary>
    public string Relation { get; } = relation;

    /// <summary>Which tuple side the entity's own Id takes.</summary>
    public SealedFgaRelationTargetType TargetType { get; } = targetType;
}
