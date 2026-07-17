using System;

namespace SealedFga.Attributes;

/// <summary>
///     Declares that a many-to-many <b>join entity</b> emits an OpenFGA tuple linking its two
///     FK ends: <c>user = &lt;userProperty&gt;</c>, <c>object = &lt;objectProperty&gt;</c> — the join
///     row's own primary key appears on neither side of the tuple.
///     <para>
///         Both named properties must be strongly-typed SealedFGA IDs (<c>ISealedFgaUser</c>).
///         The entity itself does <b>not</b> need to implement <c>ISealedFgaType&lt;TId&gt;</c>;
///         a pure join row has no OpenFGA type of its own.
///     </para>
///     <para>
///         Sync semantics (via the SaveChanges interceptor, same transactional outbox as
///         <see cref="SealedFgaRelationAttribute" />): adding the row writes the tuple (skipped
///         while either FK is <c>null</c>); re-pointing either FK deletes the old pair's tuple and
///         writes the new pair's; deleting the row deletes exactly its tuple. Tuples referencing a
///         deleted <i>end</i> entity are covered by that entity's own delete handling.
///     </para>
///     <example>
///         <code>
///         [SealedFgaJoinRelation(
///             nameof(SecretEntityIdAttributes.can_view),
///             userProperty: nameof(UserId),
///             objectProperty: nameof(SecretId))]
///         public class SecretShareEntity {
///             public Guid Id { get; set; }
///             public UserEntityId UserId { get; set; }
///             public SecretEntityId SecretId { get; set; }
///         }
///         // emits: user:&lt;UserId&gt; can_view secret:&lt;SecretId&gt;
///         </code>
///     </example>
/// </summary>
/// <param name="relation">The OpenFGA relation name, e.g. <c>nameof(SecretEntityIdAttributes.can_view)</c>.</param>
/// <param name="userProperty">Name of the FK property supplying the tuple's <c>user</c> side.</param>
/// <param name="objectProperty">Name of the FK property supplying the tuple's <c>object</c> side.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class SealedFgaJoinRelationAttribute(
    string relation,
    string userProperty,
    string objectProperty) : Attribute {
    /// <summary>The OpenFGA relation name of the emitted tuple.</summary>
    public string Relation { get; } = relation;

    /// <summary>Name of the FK property supplying the tuple's <c>user</c> side.</summary>
    public string UserProperty { get; } = userProperty;

    /// <summary>Name of the FK property supplying the tuple's <c>object</c> side.</summary>
    public string ObjectProperty { get; } = objectProperty;
}
