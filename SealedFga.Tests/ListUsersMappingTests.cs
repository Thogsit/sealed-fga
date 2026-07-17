using System.Linq;
using OpenFga.Sdk.Model;
using SealedFga.Fga;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>
///     Unit tests for <see cref="SealedFgaService.MapListUsersResult{TUserId}" /> (exposed via
///     InternalsVisibleTo): OpenFGA's three subject shapes — concrete object, userset, and typed
///     wildcard — must each be surfaced explicitly, never collapsed or silently dropped.
/// </summary>
[Collection(GlobalCollection.Name)]
public class ListUsersMappingTests {
    [Fact]
    public void Concrete_users_map_to_typed_ids() {
        var result = SealedFgaService.MapListUsersResult<TestUserId>([
            new User { Object = new FgaObject { Type = "testuser", Id = "alice" } },
            new User { Object = new FgaObject { Type = "testuser", Id = "bob" } },
        ]);

        result.Users.Select(u => u.Value).ShouldBe(["alice", "bob"]);
        result.Usersets.ShouldBeEmpty();
        result.HasWildcard.ShouldBeFalse();
    }

    [Fact]
    public void Userset_subjects_map_with_id_and_relation() {
        var result = SealedFgaService.MapListUsersResult<TestUserId>([
            new User { Userset = new UsersetUser { Type = "testuser", Id = "admins", Relation = "member" } },
        ]);

        result.Users.ShouldBeEmpty();
        var userset = result.Usersets.ShouldHaveSingleItem();
        userset.Id.Value.ShouldBe("admins");
        userset.Relation.ShouldBe("member");
        result.HasWildcard.ShouldBeFalse();
    }

    [Fact]
    public void Wildcard_sets_the_flag_rather_than_being_dropped() {
        var result = SealedFgaService.MapListUsersResult<TestUserId>([
            new User { Wildcard = new TypedWildcard { Type = "testuser" } },
        ]);

        result.HasWildcard.ShouldBeTrue();
        result.Users.ShouldBeEmpty();
    }

    [Fact]
    public void Mixed_response_surfaces_every_shape() {
        var result = SealedFgaService.MapListUsersResult<TestUserId>([
            new User { Object = new FgaObject { Type = "testuser", Id = "alice" } },
            new User { Userset = new UsersetUser { Type = "testuser", Id = "admins", Relation = "member" } },
            new User { Wildcard = new TypedWildcard { Type = "testuser" } },
        ]);

        result.Users.ShouldHaveSingleItem().Value.ShouldBe("alice");
        result.Usersets.ShouldHaveSingleItem().Id.Value.ShouldBe("admins");
        result.HasWildcard.ShouldBeTrue();
    }
}
