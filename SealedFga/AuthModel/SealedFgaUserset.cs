namespace SealedFga.AuthModel;

/// <summary>
///     Represents an OpenFGA userset; consists of the user ID and the relation.
/// </summary>
/// <typeparam name="TUserId">The type of the user ID.</typeparam>
public class SealedFgaUserset<TUserId> : ISealedFgaUser
    where TUserId : ISealedFgaTypeId<TUserId>
{
    public required TUserId Id { get; set; }
    public required ISealedFgaRelation<TUserId> Relation { get; set; }

    public static SealedFgaUserset<TUserId> From(TUserId user, ISealedFgaRelation<TUserId> relation) =>
        new() { Id = user, Relation = relation };

    public string AsOpenFgaIdTupleString() => $"{Id.AsOpenFgaIdTupleString()}#{Relation.AsOpenFgaString()}";
}
