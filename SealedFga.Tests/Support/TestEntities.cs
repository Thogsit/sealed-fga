using SealedFga.Attributes;
using SealedFga.AuthModel;

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

