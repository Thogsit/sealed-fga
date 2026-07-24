using System.Collections.Generic;
using SealedFga.Attributes;
using SealedFga.AuthModel;
using SealedFga.Fga;

namespace SealedFga.Tests.Support;

// Entities mirroring the sample's shapes:
//  - TestObjectEntity  ~ SecretEntity  (FK relation with TargetType.Object)
//  - TestUserEntity    ~ UserEntity    (FK relation with TargetType.User)
//  - TestParentEntity  ~ AgencyEntity  (no relations; the FK target)

/// <summary>FK relation with <see cref="SealedFgaRelationTargetType.Object" /> (this entity is the object).</summary>
public class TestObjectEntity : ISealedFgaType<TestObjectId> {
    [SealedFgaRelation("OwnedBy")] // TargetType.Object (default)
    public TestParentId? ParentId { get; set; }

    public string Payload { get; set; } = "";

    public TestObjectId Id { get; set; }
}

/// <summary>FK relation with <see cref="SealedFgaRelationTargetType.User" /> (this entity is the user).</summary>
public class TestUserEntity : ISealedFgaType<TestUserId> {
    [SealedFgaRelation("Member", SealedFgaRelationTargetType.User)]
    public TestParentId? ParentId { get; set; }

    public TestUserId Id { get; set; }
}

/// <summary>A relation target/parent entity with no relations of its own.</summary>
public class TestParentEntity : ISealedFgaType<TestParentId> {
    public string Name { get; set; } = "";

    public TestParentId Id { get; set; }
}

/// <summary>
///     A many-to-many join entity: the tuple links its two FK ends (<c>testuser:X can_view testobject:Y</c>);
///     its own PK is a plain <see cref="System.Guid" /> and appears in no tuple — deliberately NOT an
///     <see cref="ISealedFgaType{TId}" />.
/// </summary>
[SealedFgaJoinRelation("can_view", nameof(UserId), nameof(ObjectId))]
public class TestJoinEntity {
    public System.Guid Id { get; set; }

    public TestUserId? UserId { get; set; }

    public TestObjectId? ObjectId { get; set; }
}

/// <summary>Hand-written relation constants on <c>testobject</c>, mirroring a generated *Relations class.</summary>
public sealed class TestObjectRelation(string val) : SealedFgaRelation(val), ISealedFgaRelation<TestObjectId> {
    /// <summary>Link relation carrying the grant row itself on the tuple's <b>user</b> side.</summary>
    public static readonly TestObjectRelation ShareGrant = new("ShareGrant");

    public static readonly TestObjectRelation CanView = new("can_view");

    public static readonly TestObjectRelation CanEdit = new("can_edit");
}

/// <summary>Lifecycle states of <see cref="TestGrantEntity" /> — tuples exist iff <see cref="Active" />.</summary>
public enum TestGrantState {
    Pending = 0,
    Active = 1,
    Revoked = 2,
}

/// <summary>
///     A state-machine grant entity driving its tuples via <see cref="ISealedFgaTupleSource" /> —
///     the row is never hard-deleted in the modeled domain; activation/revocation happen as plain row
///     updates. Mirrors the consumer shape the feature was built for: a link tuple with the row's own
///     id on the <b>user</b> side, plus a permission fan-out referencing the row's id on neither side.
/// </summary>
public class TestGrantEntity : ISealedFgaType<TestGrantId>, ISealedFgaTupleSource {
    public TestGrantId Id { get; set; }

    public TestGrantState State { get; set; }

    public TestUserId UserId { get; set; }

    public TestObjectId ObjectId { get; set; }

    public bool CanEdit { get; set; }

    /// <summary>A property that never affects the desired tuples.</summary>
    public string Note { get; set; } = "";

    public IEnumerable<SealedFgaTupleOperation> DesiredTuples() {
        if (State != TestGrantState.Active) {
            yield break;
        }

        yield return SealedFgaTupleOperation.Of(Id, TestObjectRelation.ShareGrant, ObjectId);
        yield return SealedFgaTupleOperation.Of(UserId, TestObjectRelation.CanView, ObjectId);
        if (CanEdit) {
            yield return SealedFgaTupleOperation.Of(UserId, TestObjectRelation.CanEdit, ObjectId);
        }
    }
}

