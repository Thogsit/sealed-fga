using System;
using SealedFga.AuthModel;

namespace SealedFga.Fga;

/// <summary>
///     One typed <c>(user, relation, object)</c> contextual tuple for per-call query options
///     (<see cref="SealedFgaQueryOptions.ContextualTuples" />). Constructed exclusively via
///     <see cref="Of{TObjId}" /> — never from raw strings, so a malformed tuple cannot be assembled.
///     Contextual tuples are evaluated by OpenFGA as if they were stored, for that single request
///     only; nothing is ever written to the store.
/// </summary>
public readonly struct SealedFgaContextualTuple {
    internal string User { get; }
    internal string Relation { get; }
    internal string Object { get; }

    private SealedFgaContextualTuple(string user, string relation, string obj) {
        User = user;
        Relation = relation;
        Object = obj;
    }

    /// <summary>A <c>default</c>-constructed instance carries no tuple and is rejected at call time.</summary>
    internal bool IsDefault => User is null;

    /// <summary>
    ///     Creates a contextual tuple from typed parts. Userset subjects (<c>type:id#relation</c>)
    ///     work via <c>SealedFgaUserset&lt;TUserId&gt;</c> as the <paramref name="user" />.
    /// </summary>
    /// <param name="user">The tuple's user/subject.</param>
    /// <param name="relation">The relation — bound to the object type at compile time.</param>
    /// <param name="objectId">The typed object ID.</param>
    public static SealedFgaContextualTuple Of<TObjId>(
        ISealedFgaUser user,
        ISealedFgaRelation<TObjId> relation,
        TObjId objectId
    ) where TObjId : ISealedFgaTypeId<TObjId> {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(objectId);

        return new SealedFgaContextualTuple(
            user.AsOpenFgaIdTupleString(),
            relation.AsOpenFgaString(),
            objectId.AsOpenFgaIdTupleString()
        );
    }
}
