using System;
using System.Collections.Generic;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     Pure helpers for relating outbox rows to the entities they reference. Tuple columns hold
///     full OpenFGA tuple strings: objects are <c>type:id</c>, users are <c>type:id</c> or — for
///     userset subjects — <c>type:id#relation</c>. A <see cref="SealedFgaOutboxOperationType.DeleteAllForObject" />
///     row's <see cref="SealedFgaOutboxEntry.TargetId" /> is a <c>type:id</c> string.
/// </summary>
internal static class OutboxEntryMatching {
    internal static bool IsFence(SealedFgaOutboxEntry entry)
        => entry.OperationType == SealedFgaOutboxOperationType.DeleteAllForObject;

    /// <summary>
    ///     Whether a fence targeting <paramref name="fenceTargetId" /> constrains the given tuple
    ///     row — i.e. the row references the fence's entity as its object or as its user/subject
    ///     (including userset subjects <c>type:id#relation</c> of that entity).
    /// </summary>
    internal static bool FenceMatchesTupleRow(string fenceTargetId, SealedFgaOutboxEntry tupleRow)
        => tupleRow.TupleObject == fenceTargetId
           || tupleRow.TupleUser == fenceTargetId
           || (tupleRow.TupleUser?.StartsWith(fenceTargetId + "#", StringComparison.Ordinal) ?? false);

    /// <summary>
    ///     The entity tuple strings (<c>type:id</c>) a tuple row references: its object, and its
    ///     user with any userset <c>#relation</c> suffix stripped.
    /// </summary>
    internal static IEnumerable<string> EntitiesOf(SealedFgaOutboxEntry tupleRow) {
        if (tupleRow.TupleObject != null) {
            yield return tupleRow.TupleObject;
        }

        if (tupleRow.TupleUser != null) {
            var hashIndex = tupleRow.TupleUser.IndexOf('#');
            var userEntity = hashIndex >= 0 ? tupleRow.TupleUser.Substring(0, hashIndex) : tupleRow.TupleUser;
            if (userEntity != tupleRow.TupleObject) {
                yield return userEntity;
            }
        }
    }

    /// <summary>The newest-wins identity of a tuple row (its tuple key).</summary>
    internal static (string? User, string? Relation, string? Object) KeyOf(SealedFgaOutboxEntry tupleRow)
        => (tupleRow.TupleUser, tupleRow.TupleRelation, tupleRow.TupleObject);
}
