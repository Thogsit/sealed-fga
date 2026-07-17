using System;
using SealedFga.Attributes;
using SealedFga.Sample.User;

namespace SealedFga.Sample.Secret;

/// <summary>
///     A many-to-many join entity: each row grants one user direct <c>can_view</c> on one secret.
///     The class-level <see cref="SealedFgaJoinRelationAttribute" /> emits the tuple
///     <c>user:&lt;UserId&gt; can_view secret:&lt;SecretId&gt;</c> — the tuple links the two FK ends, so the
///     row's own PK stays a plain <see cref="Guid" /> and the entity needs no
///     <c>ISealedFgaType&lt;TId&gt;</c> / OpenFGA type of its own.
/// </summary>
[SealedFgaJoinRelation(
    nameof(SecretEntityIdPermissions.can_view),
    userProperty: nameof(UserId),
    objectProperty: nameof(SecretId))]
public class SecretShareEntity {
    public Guid Id { get; set; }

    public UserEntityId UserId { get; set; }

    public SecretEntityId SecretId { get; set; }
}
