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
    public TestParentId ParentId { get; set; } = null!;

    public string Payload { get; set; } = "";

    public TestObjectId Id { get; set; } = null!;
}

/// <summary>FK relation with <see cref="SealedFgaRelationTargetType.User" /> (this entity is the user).</summary>
public class TestUserEntity : ISealedFgaType<TestUserId> {
    [SealedFgaRelation("Member", SealedFgaRelationTargetType.User)]
    public TestParentId ParentId { get; set; } = null!;

    public TestUserId Id { get; set; } = null!;
}

/// <summary>A relation target/parent entity with no relations of its own.</summary>
public class TestParentEntity : ISealedFgaType<TestParentId> {
    public string Name { get; set; } = "";

    public TestParentId Id { get; set; } = null!;
}

/// <summary>
///     Like <see cref="TestObjectEntity" /> but with a surrogate EF key, so the SealedFGA <c>Id</c> is a
///     plain mutable property. This lets tests exercise the processor's primary-key-change (ModifyId)
///     branch — EF forbids mutating an entity's actual key, but the processor keys off the <c>Id</c>
///     property by name, independent of EF's key metadata.
/// </summary>
public class TestReassignableEntity : ISealedFgaType<TestObjectId> {
    public long Pk { get; set; }

    [SealedFgaRelation("OwnedBy")]
    public TestParentId ParentId { get; set; } = null!;

    public TestObjectId Id { get; set; } = null!;
}
