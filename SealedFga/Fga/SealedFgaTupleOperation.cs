using System;
using OpenFga.Sdk.Model;
using SealedFga.AuthModel;

namespace SealedFga.Fga;

/// <summary>
///     One typed <c>(user, relation, object)</c> tuple operation — the shared write currency for
///     both the immediate write path (<see cref="ISealedFgaService.WriteAsync{TObjId}" /> /
///     <see cref="ISealedFgaService.DeleteAsync{TObjId}" /> and their batch overloads), the batch
///     outbox enqueue API (<c>SealedFgaOutboxEnqueueExtensions.EnqueueFga</c>), and declarative tuple
///     sources (<see cref="AuthModel.ISealedFgaTupleSource" />). Constructed exclusively
///     via <see cref="Of{TObjId}" /> — never from raw strings, so a malformed tuple cannot be assembled.
///     <para>
///         Two operations are <b>equal</b> iff their user, relation and object parts are equal — the
///         set semantics the tuple-source diff relies on, and what makes desired-tuple sets directly
///         assertable in consumer tests.
///     </para>
/// </summary>
public readonly struct SealedFgaTupleOperation : IEquatable<SealedFgaTupleOperation> {
    internal string User { get; }
    internal string Relation { get; }
    internal string Object { get; }

    private SealedFgaTupleOperation(string user, string relation, string obj) {
        User = user;
        Relation = relation;
        Object = obj;
    }

    /// <summary>A <c>default</c>-constructed instance carries no tuple and is rejected at enqueue/write time.</summary>
    internal bool IsDefault => User is null;

    /// <summary>
    ///     Creates a tuple operation from typed parts. Userset subjects (<c>type:id#relation</c>)
    ///     work via <c>SealedFgaUserset&lt;TUserId&gt;</c> as the <paramref name="user" />.
    /// </summary>
    /// <param name="user">The tuple's user/subject.</param>
    /// <param name="relation">The relation — bound to the object type at compile time.</param>
    /// <param name="objectId">The typed object ID.</param>
    public static SealedFgaTupleOperation Of<TObjId>(
        ISealedFgaUser user,
        ISealedFgaRelation<TObjId> relation,
        TObjId objectId
    ) where TObjId : ISealedFgaTypeId<TObjId> {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(objectId);

        return new SealedFgaTupleOperation(
            user.AsOpenFgaIdTupleString(),
            relation.AsOpenFgaString(),
            objectId.AsOpenFgaIdTupleString()
        );
    }

    /// <summary>
    ///     Projects this operation to a raw <see cref="TupleKey" /> for the immediate write path.
    ///     Throws if this is a <c>default</c>-constructed instance carrying no tuple.
    /// </summary>
    internal TupleKey ToTupleKey() {
        EnsureNotDefault();
        return new TupleKey {
            User = User,
            Relation = Relation,
            Object = Object,
        };
    }

    /// <summary>
    ///     Value equality over the <c>(user, relation, object)</c> triplet.
    /// </summary>
    /// <param name="other">The operation to compare against.</param>
    /// <returns>Whether both operations describe the same tuple.</returns>
    public bool Equals(SealedFgaTupleOperation other)
        => User == other.User
           && Relation == other.Relation
           && Object == other.Object;

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is SealedFgaTupleOperation other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(User, Relation, Object);

    /// <summary>Value equality over the <c>(user, relation, object)</c> triplet.</summary>
    public static bool operator ==(SealedFgaTupleOperation left, SealedFgaTupleOperation right)
        => left.Equals(right);

    /// <summary>Value inequality over the <c>(user, relation, object)</c> triplet.</summary>
    public static bool operator !=(SealedFgaTupleOperation left, SealedFgaTupleOperation right)
        => !left.Equals(right);

    /// <summary>Throws if this is a <c>default</c>-constructed instance carrying no tuple.</summary>
    internal void EnsureNotDefault() {
        if (IsDefault) {
            throw new ArgumentException(
                $"A default-constructed {nameof(SealedFgaTupleOperation)} carries no tuple; build "
                + $"operations via {nameof(SealedFgaTupleOperation)}.{nameof(Of)}(...)."
            );
        }
    }
}
