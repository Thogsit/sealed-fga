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

        var processed = await SealedFgaOutboxDrainer.DrainOnceAsync(ctx, deadService, batchSize: 10, maxAttempts: 5);

        processed.ShouldBe(0);
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
