using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using OpenFga.Sdk.Client;
using OpenFga.Sdk.Client.Model;
using SealedFga;
using SealedFga.Fga;
using SealedFga.Fga.Outbox;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.IntegrationTests;

/// <summary>
///     Exercises <see cref="SealedFgaOutboxDrainer" /> and the end-to-end sync (change processor →
///     outbox rows → drainer → OpenFGA) against a real OpenFGA server plus a SQLite-backed outbox.
/// </summary>
[Collection(OpenFgaCollection.Name)]
[Trait("Category", "Integration")]
public class OutboxDrainerIntegrationTests(OpenFgaFixture fga) {
    [Fact]
    public async Task DrainOnce_applies_pending_write_and_marks_processed() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var obj = GuidOf("drain-write");
        ctx.Outbox.Add(SealedFgaOutboxEntry.ForWrite("testuser:frank", "can_view", $"testobject:{obj}"));
        await ctx.SaveChangesAsync();

        var processed = await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, batchSize: 10, maxAttempts: 5);

        processed.ShouldBe(1);
        ctx.Outbox.Single().ProcessedAtUtc.ShouldNotBeNull();
        (await fga.Client.Read(new ClientReadRequest { Object = $"testobject:{obj}" })).Tuples.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DrainOnce_processes_in_id_order_and_respects_batch_size() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        for (var i = 0; i < 5; i++) {
            ctx.Outbox.Add(SealedFgaOutboxEntry.ForWrite("testuser:gina", "can_view", $"testobject:{GuidOf($"batch{i}")}"));
        }
        await ctx.SaveChangesAsync();

        var processed = await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, batchSize: 3, maxAttempts: 5);

        processed.ShouldBe(3);
        ctx.Outbox.OrderBy(e => e.Id).Take(3).ShouldAllBe(e => e.ProcessedAtUtc != null);
        ctx.Outbox.OrderBy(e => e.Id).Skip(3).ShouldAllBe(e => e.ProcessedAtUtc == null);
    }

    [Fact]
    public async Task DrainOnce_treats_delete_of_never_stored_tuple_as_processed_noop() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        // Grant computed can_edit via the union arm (Member from OwnedBy) so the old
        // check-then-delete layer would have attempted (and failed) a real delete; the direct
        // tuple itself was never stored.
        var parent = GuidOf("noop-parent");
        var obj = GuidOf("noop-del");
        await fga.Service.WriteTuplesAsync([
            new OpenFga.Sdk.Model.TupleKey { User = "testuser:henry", Relation = "Member", Object = $"testparent:{parent}" },
            new OpenFga.Sdk.Model.TupleKey { User = $"testparent:{parent}", Relation = "OwnedBy", Object = $"testobject:{obj}" },
        ]);

        ctx.Outbox.Add(SealedFgaOutboxEntry.ForDelete("testuser:henry", "can_edit", $"testobject:{obj}"));
        await ctx.SaveChangesAsync();

        var processed = await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, batchSize: 10, maxAttempts: 5);

        // The row must complete as a no-op — processed, never retried, never parked.
        processed.ShouldBe(1);
        var row = ctx.Outbox.Single();
        row.ProcessedAtUtc.ShouldNotBeNull();
        row.Attempts.ShouldBe(0);
        row.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task DrainOnce_skips_rows_over_max_attempts_and_future_retries() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var exhausted = SealedFgaOutboxEntry.ForWrite("testuser:x", "can_view", "testobject:x");
        exhausted.Attempts = 5;
        var future = SealedFgaOutboxEntry.ForWrite("testuser:y", "can_view", "testobject:y");
        future.NextAttemptUtc = DateTime.UtcNow.AddHours(1);
        ctx.Outbox.AddRange(exhausted, future);
        await ctx.SaveChangesAsync();

        var processed = await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, batchSize: 10, maxAttempts: 5);

        processed.ShouldBe(0);
        ctx.Outbox.ShouldAllBe(e => e.ProcessedAtUtc == null);
    }

    [Fact]
    public async Task DrainOnce_records_failure_and_schedules_retry_when_openfga_unreachable() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        ctx.Outbox.Add(SealedFgaOutboxEntry.ForWrite("testuser:z", "can_view", "testobject:z"));
        await ctx.SaveChangesAsync();

        // A service pointed at a dead endpoint so the apply deterministically fails.
        var deadService = new SealedFgaService(
            new OpenFgaClient(new ClientConfiguration {
                ApiUrl = "http://127.0.0.1:1",
                StoreId = fga.StoreId,
                AuthorizationModelId = fga.AuthorizationModelId,
            }),
            Microsoft.Extensions.Options.Options.Create(new SealedFgaOptions())
        );

        // The return value counts state-changed rows — the failure bump counts as a change.
        var changed = await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, deadService, batchSize: 10, maxAttempts: 5);

        changed.ShouldBe(1);
        var row = ctx.Outbox.Single();
        row.ProcessedAtUtc.ShouldBeNull();
        row.Attempts.ShouldBe(1);
        row.LastError.ShouldNotBeNullOrEmpty();
        row.NextAttemptUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task End_to_end_change_processor_to_openfga() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        // Add an entity and let the processor enqueue outbox rows in the same transaction.
        var parentId = new TestParentId(GuidOf<Guid>("e2e-parent"));
        var obj = new TestObjectEntity { Id = TestObjectId.New(), ParentId = parentId, Payload = "e2e" };
        ctx.Objects.Add(obj);
        new SealedFgaSaveChangesProcessor().ProcessSealedFgaChanges(ctx);
        await ctx.SaveChangesAsync();

        // Drain to OpenFGA.
        var processed = await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, batchSize: 10, maxAttempts: 5);
        processed.ShouldBe(1);

        // The OwnedBy tuple (parent is the user, object is the object) must now exist in OpenFGA.
        var read = await fga.Client.Read(new ClientReadRequest { Object = obj.Id.AsOpenFgaIdTupleString() });
        read.Tuples.ShouldContain(t => t.Key.User == parentId.AsOpenFgaIdTupleString() && t.Key.Relation == "OwnedBy");
    }

    [Fact]
    public async Task End_to_end_join_entity_lifecycle_converges_in_openfga() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        // Adding a join row emits a write tuple linking its two FK ends (neither being its own PK).
        var userId = new TestUserId("join-e2e-user");
        var objectId = new TestObjectId(GuidOf<Guid>("join-e2e-obj"));
        var join = new TestJoinEntity { Id = Guid.NewGuid(), UserId = userId, ObjectId = objectId };
        ctx.Joins.Add(join);
        new SealedFgaSaveChangesProcessor().ProcessSealedFgaChanges(ctx);
        await ctx.SaveChangesAsync();

        (await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, batchSize: 10, maxAttempts: 5)).ShouldBe(1);
        var read = await fga.Client.Read(new ClientReadRequest { Object = objectId.AsOpenFgaIdTupleString() });
        read.Tuples.ShouldContain(t =>
            t.Key.User == userId.AsOpenFgaIdTupleString() && t.Key.Relation == "can_view");

        // Deleting the join row emits a single targeted delete of exactly that tuple.
        ctx.Joins.Remove(join);
        new SealedFgaSaveChangesProcessor().ProcessSealedFgaChanges(ctx);
        await ctx.SaveChangesAsync();

        (await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, fga.Service, batchSize: 10, maxAttempts: 5)).ShouldBe(1);
        read = await fga.Client.Read(new ClientReadRequest { Object = objectId.AsOpenFgaIdTupleString() });
        read.Tuples.ShouldNotContain(t =>
            t.Key.User == userId.AsOpenFgaIdTupleString() && t.Key.Relation == "can_view");
    }

    private static string GuidOf(string seed) => GuidOf<Guid>(seed).ToString();

    private static T GuidOf<T>(string seed) {
        var bytes = new byte[16];
        var s = seed.PadRight(16, '_');
        for (var i = 0; i < 16; i++) {
            bytes[i] = (byte) s[i % s.Length];
        }
        return (T) (object) new Guid(bytes);
    }
}
