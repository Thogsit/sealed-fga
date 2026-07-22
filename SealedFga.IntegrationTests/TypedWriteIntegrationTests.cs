using System.Threading.Tasks;
using OpenFga.Sdk.Client.Model;
using SealedFga.AuthModel;
using SealedFga.Exceptions;
using SealedFga.Fga;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.IntegrationTests;

/// <summary>
///     Exercises the strongly-typed write/delete surface of <see cref="ISealedFgaService" />
///     (<see cref="ISealedFgaService.WriteAsync{TObjId}" />, <see cref="ISealedFgaService.DeleteAsync{TObjId}" />,
///     the batch overloads, and <see cref="ISealedFgaService.ApplyAsync" />) against a real OpenFGA
///     server, proving they build the correct tuples, hit the same idempotent write path as the raw
///     path, and preserve the fail-loud <see cref="FgaWriteException" /> contract.
/// </summary>
[Collection(OpenFgaCollection.Name)]
[Trait("Category", "Integration")]
public class TypedWriteIntegrationTests(OpenFgaFixture fga) {
    [Fact]
    public async Task Typed_single_write_then_delete_round_trips_and_is_idempotent() {
        var user = new TestUserId("typed-alice");
        var obj = TestObjectId.New();
        var canView = new ObjRelation("can_view");

        await fga.Service.WriteAsync(user, canView, obj);
        await fga.Service.WriteAsync(user, canView, obj); // second write is a server-side no-op

        (await fga.Service.CheckAsync(user, canView, obj)).ShouldBeTrue();
        (await fga.Client.Read(new ClientReadRequest { Object = obj.AsOpenFgaIdTupleString() }))
            .Tuples.Count.ShouldBe(1);

        await fga.Service.DeleteAsync(user, canView, obj);
        await fga.Service.DeleteAsync(user, canView, obj); // second delete is a no-op

        (await fga.Service.CheckAsync(user, canView, obj)).ShouldBeFalse();
    }

    [Fact]
    public async Task Typed_batch_write_and_delete_build_the_right_tuples() {
        var canView = new ObjRelation("can_view");
        var alice = new TestUserId("typed-batch-alice");
        var bob = new TestUserId("typed-batch-bob");
        var obj = TestObjectId.New();

        await fga.Service.WriteAsync([
            SealedFgaTupleOperation.Of(alice, canView, obj),
            SealedFgaTupleOperation.Of(bob, canView, obj),
        ]);

        (await fga.Service.CheckAsync(alice, canView, obj)).ShouldBeTrue();
        (await fga.Service.CheckAsync(bob, canView, obj)).ShouldBeTrue();

        await fga.Service.DeleteAsync([SealedFgaTupleOperation.Of(bob, canView, obj)]);

        (await fga.Service.CheckAsync(alice, canView, obj)).ShouldBeTrue();
        (await fga.Service.CheckAsync(bob, canView, obj)).ShouldBeFalse();
    }

    [Fact]
    public async Task ApplyAsync_sends_writes_and_deletes_in_one_request() {
        var canView = new ObjRelation("can_view");
        var user = new TestUserId("typed-apply");
        var toDelete = TestObjectId.New();
        var toWrite = TestObjectId.New();

        // Seed the tuple that ApplyAsync will delete.
        await fga.Service.WriteAsync(user, canView, toDelete);
        (await fga.Service.CheckAsync(user, canView, toDelete)).ShouldBeTrue();

        await fga.Service.ApplyAsync(
            [SealedFgaTupleOperation.Of(user, canView, toWrite)],
            [SealedFgaTupleOperation.Of(user, canView, toDelete)]
        );

        (await fga.Service.CheckAsync(user, canView, toWrite)).ShouldBeTrue();
        (await fga.Service.CheckAsync(user, canView, toDelete)).ShouldBeFalse();
    }

    [Fact]
    public async Task Write_to_an_undefined_relation_throws_FgaWriteException() {
        var user = new TestUserId("typed-fail");
        var obj = TestObjectId.New();
        // "bogus" is not a relation on testobject, so the server rejects the tuple; the
        // non-transactional write path surfaces it as a per-tuple failure → FgaWriteException.
        var bogus = new ObjRelation("bogus");

        await Should.ThrowAsync<FgaWriteException>(async () =>
            await fga.Service.WriteAsync([SealedFgaTupleOperation.Of(user, bogus, obj)]));
    }
}
