using System.Linq;
using System.Threading.Tasks;
using OpenFga.Sdk.Client.Model;
using SealedFga.AuthModel;
using SealedFga.Exceptions;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;
using TupleKey = OpenFga.Sdk.Model.TupleKey;

namespace SealedFga.IntegrationTests;

internal sealed class ObjRelation(string v) : SealedFgaRelation(v), ISealedFgaRelation<TestObjectId>;

/// <summary>Exercises <see cref="SealedFga.Fga.SealedFgaService" /> against a real OpenFGA server.</summary>
[Collection(OpenFgaCollection.Name)]
[Trait("Category", "Integration")]
public class ServiceIntegrationTests(OpenFgaFixture fga) {
    [Fact]
    public async Task CheckAsync_reflects_written_tuples() {
        var objId = GuidOf("check1");
        await fga.Service.SafeWriteTupleAsync([
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
        await fga.Service.SafeWriteTupleAsync([
            new TupleKey { User = "testuser:diana", Relation = "can_view", Object = $"testobject:{objId}" },
        ]);

        var objects = (await fga.Service.ListObjectsAsync(new TestUserId("diana"), new ObjRelation("can_view"))).ToList();

        objects.ShouldContain(o => o.Value == System.Guid.Parse(objId));
    }

    [Fact]
    public async Task SafeWrite_and_SafeDelete_are_idempotent() {
        var tk = new TupleKey { User = "testuser:carol", Relation = "can_view", Object = $"testobject:{GuidOf("idemp")}" };

        await fga.Service.SafeWriteTupleAsync([tk]);
        await fga.Service.SafeWriteTupleAsync([tk]); // second write must be a no-op, not an error

        var afterWrite = await fga.Client.Read(new ClientReadRequest { Object = tk.Object });
        afterWrite.Tuples.Count.ShouldBe(1);

        await fga.Service.SafeDeleteTupleAsync([tk]);
        await fga.Service.SafeDeleteTupleAsync([tk]); // second delete must be a no-op

        var afterDelete = await fga.Client.Read(new ClientReadRequest { Object = tk.Object });
        afterDelete.Tuples.ShouldBeEmpty();
    }

    [Fact]
    public async Task ModifyIdAsync_rewrites_tuples_from_old_to_new_object() {
        var oldObj = GuidOf("modold");
        var newObj = GuidOf("modnew");
        await fga.Service.SafeWriteTupleAsync([
            new TupleKey { User = "testparent:11111111-1111-1111-1111-111111111111", Relation = "OwnedBy", Object = $"testobject:{oldObj}" },
        ]);

        await fga.Service.ModifyIdAsync(TestObjectId.Parse(oldObj), TestObjectId.Parse(newObj));

        (await fga.Client.Read(new ClientReadRequest { Object = $"testobject:{oldObj}" })).Tuples.ShouldBeEmpty();
        (await fga.Client.Read(new ClientReadRequest { Object = $"testobject:{newObj}" })).Tuples.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DeleteAllRelationsForRawObject_purges_object_and_user_side_tuples() {
        var user = "erin";
        var parent = "22222222-2222-2222-2222-222222222222";
        var obj = GuidOf("delall");
        await fga.Service.SafeWriteTupleAsync([
            new TupleKey { User = $"testuser:{user}", Relation = "can_view", Object = $"testobject:{obj}" }, // user side
            new TupleKey { User = $"testuser:{user}", Relation = "Member", Object = $"testparent:{parent}" }, // user side
        ]);

        await fga.Service.DeleteAllRelationsForRawObjectAsync($"testuser:{user}", "testuser");

        (await fga.Client.Read(new ClientReadRequest { Object = $"testobject:{obj}" })).Tuples.ShouldBeEmpty();
        (await fga.Client.Read(new ClientReadRequest { Object = $"testparent:{parent}" })).Tuples.ShouldBeEmpty();
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
