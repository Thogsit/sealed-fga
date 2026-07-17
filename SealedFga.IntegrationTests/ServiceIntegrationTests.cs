using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using OpenFga.Sdk.Client.Model;
using OpenFga.Sdk.Model;
using SealedFga;
using SealedFga.AuthModel;
using SealedFga.Exceptions;
using SealedFga.Fga;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;
using TupleKey = OpenFga.Sdk.Model.TupleKey;

namespace SealedFga.IntegrationTests;

internal sealed class ObjRelation(string v) : SealedFgaRelation(v), ISealedFgaRelation<TestObjectId>;

internal sealed class ParentRelation(string v) : SealedFgaRelation(v), ISealedFgaRelation<TestParentId>;

/// <summary>Exercises <see cref="SealedFga.Fga.SealedFgaService" /> against a real OpenFGA server.</summary>
[Collection(OpenFgaCollection.Name)]
[Trait("Category", "Integration")]
public class ServiceIntegrationTests(OpenFgaFixture fga) {
    [Fact]
    public async Task CheckAsync_reflects_written_tuples() {
        var objId = GuidOf("check1");
        await fga.Service.WriteTuplesAsync([
            new TupleKey { User = "testuser:alice", Relation = "can_view", Object = $"testobject:{objId}" },
        ]);

        (await fga.Service.CheckAsync(new TestUserId("alice"), new ObjRelation("can_view"), TestObjectId.Parse(objId)))
            .ShouldBeTrue();
        (await fga.Service.CheckAsync(new TestUserId("bob"), new ObjRelation("can_view"), TestObjectId.Parse(objId)))
            .ShouldBeFalse();
        (await fga.Service.CheckAsync(new TestUserId("alice"), new ObjRelation("can_view"), TestObjectId.New()))
            .ShouldBeFalse(); // a different, unwritten object
    }

    [Fact]
    public async Task EnsureCheckAsync_throws_when_denied() {
        await Should.ThrowAsync<FgaForbiddenException>(async () =>
            await fga.Service.EnsureCheckAsync(new TestUserId("nobody"), new ObjRelation("can_view"), new TestObjectId(default)));
    }

    [Fact]
    public async Task ListObjectsAsync_returns_authorized_objects_as_strong_ids() {
        var objId = GuidOf("list1");
        await fga.Service.WriteTuplesAsync([
            new TupleKey { User = "testuser:diana", Relation = "can_view", Object = $"testobject:{objId}" },
        ]);

        var objects = (await fga.Service.ListObjectsAsync(new TestUserId("diana"), new ObjRelation("can_view"))).ToList();

        objects.ShouldContain(o => o.Value == System.Guid.Parse(objId));
    }

    [Fact]
    public async Task Write_and_Delete_are_idempotent() {
        var tk = new TupleKey { User = "testuser:carol", Relation = "can_view", Object = $"testobject:{GuidOf("idemp")}" };

        await fga.Service.WriteTuplesAsync([tk]);
        await fga.Service.WriteTuplesAsync([tk]); // second write must be a no-op, not an error

        var afterWrite = await fga.Client.Read(new ClientReadRequest { Object = tk.Object });
        afterWrite.Tuples.Count.ShouldBe(1);

        await fga.Service.DeleteTuplesAsync([tk]);
        await fga.Service.DeleteTuplesAsync([tk]); // second delete must be a no-op

        var afterDelete = await fga.Client.Read(new ClientReadRequest { Object = tk.Object });
        afterDelete.Tuples.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteAllRelationsForRawObject_purges_object_and_user_side_tuples() {
        var user = "erin";
        var parent = "22222222-2222-2222-2222-222222222222";
        var obj = GuidOf("delall");
        await fga.Service.WriteTuplesAsync([
            new TupleKey { User = $"testuser:{user}", Relation = "can_view", Object = $"testobject:{obj}" }, // user side
            new TupleKey { User = $"testuser:{user}", Relation = "Member", Object = $"testparent:{parent}" }, // user side
        ]);

        await fga.Service.DeleteAllRelationsForRawObjectAsync($"testuser:{user}", "testuser");

        (await fga.Client.Read(new ClientReadRequest { Object = $"testobject:{obj}" })).Tuples.ShouldBeEmpty();
        (await fga.Client.Read(new ClientReadRequest { Object = $"testparent:{parent}" })).Tuples.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteAllRelations_handles_more_tuples_than_a_single_read_page_and_write_batch() {
        // 150 tuples on one object exceeds both a single OpenFGA Read page and the per-Write
        // transaction limit (~100). Exercises paginated reads (find every stored tuple) and chunked
        // writes (split the delete into multiple transactions).
        var obj = GuidOf("bigdelete");
        const int count = 150;
        var tuples = Enumerable.Range(0, count)
                               .Select(i => new TupleKey {
                                    User = $"testuser:bulk-{i}",
                                    Relation = "can_view",
                                    Object = $"testobject:{obj}",
                                })
                               .ToList();

        // The write itself is >100 operations, so it also exercises write chunking on the write path.
        await fga.Service.WriteTuplesAsync(tuples);

        // Sanity check via paginated read that all 150 landed.
        var stored = await fga.Service.ListAllRelationsToObjectAsync($"testobject:{obj}");
        stored.Count.ShouldBe(count);

        await fga.Service.DeleteAllRelationsForRawObjectAsync($"testobject:{obj}", "testobject");

        (await fga.Service.ListAllRelationsToObjectAsync($"testobject:{obj}")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Write_materializes_stored_tuple_when_computed_grant_already_exists() {
        // can_edit is a union: [testuser] or Member from OwnedBy. Grant fred computed access
        // via his parent's ownership, so a Check on the direct relation is already true.
        var parent = GuidOf("uniow-parent");
        var obj = GuidOf("union-write");
        var direct = new TupleKey { User = "testuser:fred", Relation = "can_edit", Object = $"testobject:{obj}" };
        var member = new TupleKey { User = "testuser:fred", Relation = "Member", Object = $"testparent:{parent}" };
        await fga.Service.WriteTuplesAsync([
            member,
            new TupleKey { User = $"testparent:{parent}", Relation = "OwnedBy", Object = $"testobject:{obj}" },
        ]);
        (await fga.Service.CheckAsync(new TestUserId("fred"), new ObjRelation("can_edit"), TestObjectId.Parse(obj)))
            .ShouldBeTrue(); // computed arm already grants access

        // The direct grant must still be materialized as a STORED tuple (the old check-then-write
        // layer skipped it because Check evaluated the computed relation).
        await fga.Service.WriteTuplesAsync([direct]);

        var stored = await fga.Client.Read(new ClientReadRequest { User = direct.User, Object = direct.Object });
        stored.Tuples.ShouldContain(t => t.Key.Relation == "can_edit");

        // Revoking the computed path must leave the independent direct grant intact.
        await fga.Service.DeleteTuplesAsync([member]);
        (await fga.Service.CheckAsync(new TestUserId("fred"), new ObjRelation("can_edit"), TestObjectId.Parse(obj)))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_of_never_stored_tuple_with_computed_grant_is_noop() {
        // grace can_edit the object only via the computed arm; no direct tuple is ever stored.
        var parent = GuidOf("unidel-parent");
        var obj = GuidOf("union-delete");
        await fga.Service.WriteTuplesAsync([
            new TupleKey { User = "testuser:grace", Relation = "Member", Object = $"testparent:{parent}" },
            new TupleKey { User = $"testparent:{parent}", Relation = "OwnedBy", Object = $"testobject:{obj}" },
        ]);

        // Deleting the never-stored direct tuple must be a no-op (OnMissingDeletes = Ignore),
        // not an error — the old layer attempted the delete because Check saw the computed arm.
        await fga.Service.DeleteTuplesAsync([
            new TupleKey { User = "testuser:grace", Relation = "can_edit", Object = $"testobject:{obj}" },
        ]);

        (await fga.Service.CheckAsync(new TestUserId("grace"), new ObjRelation("can_edit"), TestObjectId.Parse(obj)))
            .ShouldBeTrue(); // computed access untouched
    }

    [Fact]
    public async Task Contextual_tuple_grants_access_for_the_single_call_only() {
        // can_edit is a union: [testuser] or Member from OwnedBy. Store only the OwnedBy leg and
        // supply the Member leg as a contextual tuple — the computed arm fires for that call only.
        var parent = GuidOf("ctx-parent");
        var obj = GuidOf("ctx-check");
        var user = new TestUserId("ctx-henry");
        await fga.Service.WriteTuplesAsync([
            new TupleKey { User = $"testparent:{parent}", Relation = "OwnedBy", Object = $"testobject:{obj}" },
        ]);
        var options = new SealedFgaQueryOptions {
            ContextualTuples = [
                SealedFgaContextualTuple.Of(user, new ParentRelation("Member"), TestParentId.Parse(parent)),
            ],
        };

        (await fga.Service.CheckAsync(user, new ObjRelation("can_edit"), TestObjectId.Parse(obj)))
            .ShouldBeFalse(); // no stored membership
        (await fga.Service.CheckAsync(user, new ObjRelation("can_edit"), TestObjectId.Parse(obj), options))
            .ShouldBeTrue(); // contextual membership activates the computed arm
        (await fga.Service.CheckAsync(user, new ObjRelation("can_edit"), TestObjectId.Parse(obj)))
            .ShouldBeFalse(); // nothing was stored by the contextual call
    }

    [Fact]
    public async Task ListObjectsAsync_honors_contextual_tuples() {
        var obj = GuidOf("ctx-list");
        var user = new TestUserId("ctx-irene");
        var options = new SealedFgaQueryOptions {
            ContextualTuples = [
                SealedFgaContextualTuple.Of(user, new ObjRelation("can_view"), TestObjectId.Parse(obj)),
            ],
        };

        (await fga.Service.ListObjectsAsync(user, new ObjRelation("can_view"))).ShouldBeEmpty();

        var objects = (await fga.Service.ListObjectsAsync(user, new ObjRelation("can_view"), options)).ToList();
        objects.ShouldContain(o => o.Value == System.Guid.Parse(obj));
    }

    [Fact]
    public async Task BatchCheckAsync_applies_contextual_tuples_to_every_item() {
        var objA = TestObjectId.Parse(GuidOf("ctx-batchA"));
        var objB = TestObjectId.Parse(GuidOf("ctx-batchB"));
        var user = new TestUserId("ctx-june");
        var canView = new ObjRelation("can_view");
        var options = new SealedFgaQueryOptions {
            ContextualTuples = [SealedFgaContextualTuple.Of(user, canView, objA)],
        };

        var results = await fga.Service.BatchCheckAsync([
                ((ISealedFgaUser) user, (ISealedFgaRelation<TestObjectId>) canView, objA),
                (user, canView, objB),
            ],
            options
        );

        results[(user, canView, objA)].ShouldBeTrue(); // granted contextually
        results[(user, canView, objB)].ShouldBeFalse(); // untouched by the contextual tuple
    }

    [Fact]
    public async Task Higher_consistency_round_trips_on_check_list_and_batch() {
        var obj = GuidOf("consistency");
        var user = new TestUserId("ctx-karl");
        var canView = new ObjRelation("can_view");
        await fga.Service.WriteTuplesAsync([
            new TupleKey { User = user.AsOpenFgaIdTupleString(), Relation = "can_view", Object = $"testobject:{obj}" },
        ]);
        var options = new SealedFgaQueryOptions {
            Consistency = ConsistencyPreference.HIGHERCONSISTENCY,
        };

        (await fga.Service.CheckAsync(user, canView, TestObjectId.Parse(obj), options)).ShouldBeTrue();
        (await fga.Service.ListObjectsAsync(user, canView, options))
            .ShouldContain(o => o.Value == System.Guid.Parse(obj));
        var results = await fga.Service.BatchCheckAsync(
            [((ISealedFgaUser) user, (ISealedFgaRelation<TestObjectId>) canView, TestObjectId.Parse(obj))],
            options
        );
        results.Single().Value.ShouldBeTrue();
    }

    [Fact]
    public async Task ListUsersAsync_returns_concrete_subjects_as_strong_ids() {
        var obj = GuidOf("listusers");
        await fga.Service.WriteTuplesAsync([
            new TupleKey { User = "testuser:uma", Relation = "can_view", Object = $"testobject:{obj}" },
            new TupleKey { User = "testuser:victor", Relation = "can_view", Object = $"testobject:{obj}" },
        ]);

        var result = await fga.Service.ListUsersAsync<TestObjectId, TestUserId>(
            TestObjectId.Parse(obj),
            new ObjRelation("can_view")
        );

        result.Users.Select(u => u.Value).ShouldBe(["uma", "victor"], ignoreOrder: true);
        result.HasWildcard.ShouldBeFalse();
        result.Usersets.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListUsersAsync_honors_contextual_tuples() {
        var obj = GuidOf("listusers-ctx");
        var user = new TestUserId("wendy");
        var options = new SealedFgaQueryOptions {
            ContextualTuples = [
                SealedFgaContextualTuple.Of(user, new ObjRelation("can_view"), TestObjectId.Parse(obj)),
            ],
        };

        (await fga.Service.ListUsersAsync<TestObjectId, TestUserId>(TestObjectId.Parse(obj), new ObjRelation("can_view")))
            .Users.ShouldBeEmpty();

        var result = await fga.Service.ListUsersAsync<TestObjectId, TestUserId>(
            TestObjectId.Parse(obj),
            new ObjRelation("can_view"),
            options
        );
        result.Users.Select(u => u.Value).ShouldContain("wendy");
    }

    [Fact]
    public async Task ListRelationsAsync_returns_only_the_relations_the_user_holds() {
        var obj = GuidOf("listrelations");
        var user = new TestUserId("xena");
        await fga.Service.WriteTuplesAsync([
            new TupleKey { User = user.AsOpenFgaIdTupleString(), Relation = "can_view", Object = $"testobject:{obj}" },
        ]);

        ISealedFgaRelation<TestObjectId>[] candidates = [new ObjRelation("can_view"), new ObjRelation("can_edit")];
        var held = await fga.Service.ListRelationsAsync(user, candidates, TestObjectId.Parse(obj));

        held.Select(r => r.AsOpenFgaString()).ShouldBe(["can_view"]);
    }

    [Fact]
    public async Task BatchCheckAsync_applies_per_item_contextual_tuples() {
        var objA = TestObjectId.Parse(GuidOf("peritemA"));
        var objB = TestObjectId.Parse(GuidOf("peritemB"));
        var user = new TestUserId("peritem-yuri");
        var canView = new ObjRelation("can_view");

        // Each check carries a contextual grant only for object A (the "tuple on its own object"
        // shape). B gets none, so per-item application means A is allowed and B is not.
        var results = await fga.Service.BatchCheckAsync(
            [((ISealedFgaUser) user, (ISealedFgaRelation<TestObjectId>) canView, objA), (user, canView, objB)],
            check => check.Object.Equals(objA)
                ? [SealedFgaContextualTuple.Of(user, canView, objA)]
                : null
        );

        results[(user, canView, objA)].ShouldBeTrue();
        results[(user, canView, objB)].ShouldBeFalse();
    }

    [Fact]
    public async Task DefaultListConsistency_is_honored_by_list_operations() {
        // A service configured to default list ops to HIGHERCONSISTENCY (mirroring a consumer that
        // "always uses HIGHERCONSISTENCY on list ops") must still round-trip ListObjects/ListUsers/
        // ListRelations without an explicit per-call consistency.
        var service = new SealedFgaService(fga.Client, Options.Create(new SealedFgaOptions {
            DefaultListConsistency = ConsistencyPreference.HIGHERCONSISTENCY,
        }));
        var obj = GuidOf("defaultconsistency");
        var user = new TestUserId("zoe");
        await service.WriteTuplesAsync([
            new TupleKey { User = user.AsOpenFgaIdTupleString(), Relation = "can_view", Object = $"testobject:{obj}" },
        ]);

        (await service.ListObjectsAsync(user, new ObjRelation("can_view")))
            .ShouldContain(o => o.Value == System.Guid.Parse(obj));
        (await service.ListUsersAsync<TestObjectId, TestUserId>(TestObjectId.Parse(obj), new ObjRelation("can_view")))
            .Users.Select(u => u.Value).ShouldContain("zoe");
        (await service.ListRelationsAsync(
                user,
                [(ISealedFgaRelation<TestObjectId>) new ObjRelation("can_view")],
                TestObjectId.Parse(obj)
            ))
            .Select(r => r.AsOpenFgaString()).ShouldBe(["can_view"]);
    }

    // Deterministic, unique GUID string per logical key so tests don't collide in the shared store.
    private static string GuidOf(string seed) {
        var bytes = new byte[16];
        var s = seed.PadRight(16, '_');
        for (var i = 0; i < 16; i++) {
            bytes[i] = (byte) s[i];
        }
        return new System.Guid(bytes).ToString();
    }
}
