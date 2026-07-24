using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using OpenFga.Sdk.Client.Model;
using OpenFga.Sdk.Model;
using SealedFga.Fga;
using SealedFga.Fga.Outbox;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.IntegrationTests;

/// <summary>
///     End-to-end state-machine round-trip for <c>ISealedFgaTupleSource</c> against a real OpenFGA:
///     a grant row that is never hard-deleted drives its tuples purely through row updates — the
///     SaveChanges processor diffs <c>DesiredTuples()</c> into outbox rows, and the real drainer
///     applies them. Covers both tuple orientations: the link tuple carries the grant's own id on
///     the <b>user</b> side, the permission fan-out references the grant's id on neither side.
/// </summary>
[Collection(OpenFgaCollection.Name)]
[Trait("Category", "Integration")]
public class TupleSourceIntegrationTests(OpenFgaFixture fga) {
    private static void Process(DbContext ctx) => new SealedFgaSaveChangesProcessor().ProcessSealedFgaChanges(ctx);

    /// <summary>Mimics one interceptor-backed SaveChanges: diff desired tuples, save, drain to OpenFGA.</summary>
    private async Task<int> SaveAndDrainAsync(TestDbContext ctx) {
        Process(ctx);
        await ctx.SaveChangesAsync();
        return await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, batchSize: 100, maxAttempts: 5);
    }

    private async Task<TupleKey[]> StoredTuplesOnAsync(TestObjectId obj) {
        var read = await fga.Client.Read(new ClientReadRequest { Object = obj.AsOpenFgaIdTupleString() });
        return read.Tuples.Select(t => t.Key).ToArray();
    }

    [Fact]
    public async Task Grant_state_machine_round_trip_converges_tuples_and_keeps_the_row() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var grant = new TestGrantEntity {
            Id = TestGrantId.New(),
            State = TestGrantState.Pending,
            UserId = new TestUserId("sm-user"),
            ObjectId = TestObjectId.New(),
            CanEdit = false,
        };
        var grantUser = grant.Id.AsOpenFgaIdTupleString();
        var recipient = grant.UserId.AsOpenFgaIdTupleString();

        // 1) Insert inactive: the row exists, no tuples do.
        ctx.Grants.Add(grant);
        (await SaveAndDrainAsync(ctx)).ShouldBe(0);
        (await StoredTuplesOnAsync(grant.ObjectId)).ShouldBeEmpty();

        // 2) Activate by row update: link tuple (grant on the USER side) + can_view appear.
        grant.State = TestGrantState.Active;
        (await SaveAndDrainAsync(ctx)).ShouldBe(2);
        var tuples = await StoredTuplesOnAsync(grant.ObjectId);
        tuples.ShouldContain(t => t.User == grantUser && t.Relation == "ShareGrant");
        tuples.ShouldContain(t => t.User == recipient && t.Relation == "can_view");
        tuples.Length.ShouldBe(2);

        // 3) Widen the permission set: exactly the difference (can_edit) is applied.
        grant.CanEdit = true;
        (await SaveAndDrainAsync(ctx)).ShouldBe(1);
        tuples = await StoredTuplesOnAsync(grant.ObjectId);
        tuples.ShouldContain(t => t.User == recipient && t.Relation == "can_edit");
        tuples.Length.ShouldBe(3);
        (await fga.Service.CheckAsync(grant.UserId, TestObjectRelation.CanEdit, grant.ObjectId)).ShouldBeTrue();

        // 4) Revoke by row update: every tuple disappears...
        grant.State = TestGrantState.Revoked;
        (await SaveAndDrainAsync(ctx)).ShouldBe(3);
        (await StoredTuplesOnAsync(grant.ObjectId)).ShouldBeEmpty();
        (await fga.Service.CheckAsync(grant.UserId, TestObjectRelation.CanView, grant.ObjectId)).ShouldBeFalse();

        // ...while the row survives as the audit record of the state machine.
        (await ctx.Grants.SingleAsync(g => g.Id == grant.Id)).State.ShouldBe(TestGrantState.Revoked);
    }

    [Fact]
    public async Task Retargeting_an_active_grant_moves_its_tuples() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var grant = new TestGrantEntity {
            Id = TestGrantId.New(),
            State = TestGrantState.Active,
            UserId = new TestUserId("sm-mover"),
            ObjectId = TestObjectId.New(),
        };
        var oldObject = grant.ObjectId;

        ctx.Grants.Add(grant);
        (await SaveAndDrainAsync(ctx)).ShouldBe(2);

        var newObject = TestObjectId.New();
        grant.ObjectId = newObject;
        (await SaveAndDrainAsync(ctx)).ShouldBe(4); // 2 deletes + 2 writes

        (await StoredTuplesOnAsync(oldObject)).ShouldBeEmpty();
        var moved = await StoredTuplesOnAsync(newObject);
        moved.ShouldContain(t => t.User == grant.Id.AsOpenFgaIdTupleString() && t.Relation == "ShareGrant");
        moved.ShouldContain(t => t.User == grant.UserId.AsOpenFgaIdTupleString() && t.Relation == "can_view");
    }
}
