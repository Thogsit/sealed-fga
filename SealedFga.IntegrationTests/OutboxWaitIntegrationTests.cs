using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenFga.Sdk.Client.Model;
using SealedFga.AuthModel;
using SealedFga.Fga;
using SealedFga.Fga.Outbox;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.IntegrationTests;

/// <summary>
///     End-to-end cover for the read-after-write guarantee: when
///     <see cref="SealedFgaOutboxWaiter{TDbContext}" /> returns, the tuple the caller just enqueued is
///     genuinely live in OpenFGA, so an immediate permission check on the new entity succeeds.
///     <para>
///         The drainer here is driven by polling alone — the enqueuing context carries no signal
///         interceptor — and the poll interval is set well above the assertion that runs before the
///         wait. That is what gives these teeth: a wait that returned early (or did nothing) would
///         find the tuple still absent and fail, rather than passing by luck.
///     </para>
/// </summary>
[Collection(OpenFgaCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OutboxWaitIntegrationTests(OpenFgaFixture fga, PostgresFixture pg)
    : IClassFixture<PostgresFixture> {
    /// <summary>Long enough that the pre-wait "not yet applied" assertion cannot race the drainer.</summary>
    private static readonly TimeSpan SlowPollInterval = TimeSpan.FromSeconds(3);

    /// <summary>Comfortably above the poll interval, so only a broken wait times out.</summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    private sealed class TestObjectRelation(string val) : SealedFgaRelation(val), ISealedFgaRelation<TestObjectId>;

    private static readonly TestObjectRelation CanView = new("can_view");

    [Fact]
    public async Task The_wait_returns_only_once_the_tuple_is_live_in_openfga() {
        var options = Options.Create(new SealedFgaOptions {
            RunOutboxDrainer = true,
            OutboxPollInterval = SlowPollInterval,
            OutboxWaitTimeout = WaitTimeout,
            OutboxWaitPollInterval = TimeSpan.FromMilliseconds(25),
        });

        await using var ctx = await pg.CreateCleanContextAsync();
        var pendingRows = new SealedFgaOutboxPendingRows();

        await using var host = await StartedDrainerAsync(options);

        var user = new TestUserId($"wait-{Guid.NewGuid():N}");
        var obj = TestObjectId.New();

        await using (var writer = pg.CreateContextWithInterceptors(
                         new SealedFgaOutboxTrackingInterceptor(pendingRows, options)
                     )) {
            writer.EnqueueFgaWrite(user, CanView, obj);
            await writer.SaveChangesAsync();
        }

        // Nothing has drained yet — the drainer is a full poll interval away and was never signalled.
        (await TupleExistsAsync(user, obj)).ShouldBeFalse();

        var waiter = new SealedFgaOutboxWaiter<TestDbContext>(ctx, pendingRows, options);
        (await waiter.WaitForPendingAsync()).ShouldBeTrue();

        // The guarantee: by the time the wait returns, the check the caller is about to make passes.
        (await TupleExistsAsync(user, obj)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_scope_that_enqueued_nothing_does_not_wait() {
        var options = Options.Create(new SealedFgaOptions {
            RunOutboxDrainer = true,
            OutboxPollInterval = SlowPollInterval,
            OutboxWaitTimeout = WaitTimeout,
        });

        await using var ctx = await pg.CreateCleanContextAsync();
        var waiter = new SealedFgaOutboxWaiter<TestDbContext>(ctx, new SealedFgaOutboxPendingRows(), options);

        var start = DateTime.UtcNow;
        (await waiter.WaitForPendingAsync()).ShouldBeTrue();

        // A read-only request must not pay the drain latency.
        (DateTime.UtcNow - start).ShouldBeLessThan(SlowPollInterval);
    }

    private async Task<bool> TupleExistsAsync(TestUserId user, TestObjectId obj) {
        var read = await fga.Client.Read(new ClientReadRequest { Object = obj.AsOpenFgaIdTupleString() });
        return read.Tuples.Any(t =>
            t.Key.User == user.AsOpenFgaIdTupleString() && t.Key.Relation == "can_view"
        );
    }

    private async Task<DrainerHost> StartedDrainerAsync(IOptions<SealedFgaOptions> options) {
        var services = new ServiceCollection();
        services.AddScoped(_ => pg.CreateContext());
        services.AddScoped(_ => fga.Service);
        var provider = services.BuildServiceProvider();

        var service = new SealedFgaOutboxHostedService<TestDbContext>(
            provider,
            options,
            new SealedFgaOutboxSignal()
        );

        var host = new DrainerHost(service, provider);
        await host.StartAsync();
        return host;
    }

    /// <summary>Owns the hosted service and its provider so both are torn down with the test.</summary>
    private sealed class DrainerHost(
        SealedFgaOutboxHostedService<TestDbContext> service,
        ServiceProvider provider
    ) : IAsyncDisposable {
        public async Task StartAsync() {
            await service.StartAsync(CancellationToken.None);
            // Let the startup pass complete, so the assertions below are about the wait rather than
            // about racing the drainer's very first drain.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        public async ValueTask DisposeAsync() {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
            await provider.DisposeAsync();
        }
    }
}
