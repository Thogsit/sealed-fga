using System;
using System.Diagnostics;
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
///     End-to-end cover for the low-latency drain path: committing a tuple change must reach OpenFGA
///     without waiting out <see cref="SealedFgaOptions.OutboxPollInterval" />. The poll interval is
///     set far longer than the test's own deadline, so these only pass if the wake-up signal fired —
///     a regression that silently reverts to polling turns them red rather than slow.
/// </summary>
[Collection(OpenFgaCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OutboxSignalHostedServiceTests(OpenFgaFixture fga, PostgresFixture pg)
    : IClassFixture<PostgresFixture> {
    /// <summary>Long enough that a poll-driven drain cannot be mistaken for a signal-driven one.</summary>
    private static readonly TimeSpan UnreachablePollInterval = TimeSpan.FromMinutes(5);

    /// <summary>Budget for the signalled drain; two orders of magnitude under the poll interval.</summary>
    private static readonly TimeSpan DrainDeadline = TimeSpan.FromSeconds(20);

    /// <summary>Lets the hosted service finish its startup pass and settle into its wait.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2);

    private sealed class TestObjectRelation(string val) : SealedFgaRelation(val), ISealedFgaRelation<TestObjectId>;

    private static readonly TestObjectRelation CanView = new("can_view");

    [Fact]
    public async Task A_committed_enqueue_is_applied_without_waiting_for_the_next_poll() {
        var signal = new SealedFgaOutboxSignal();
        await using var ctx = await pg.CreateCleanContextAsync();
        await using var host = StartedDrainer(signal);
        await host.StartedAsync;

        var user = new TestUserId($"alice-{Guid.NewGuid():N}");
        var obj = TestObjectId.New();

        var stopwatch = Stopwatch.StartNew();
        await using (var writer = pg.CreateContextWithInterceptors(new SealedFgaOutboxSignalInterceptor(signal))) {
            writer.EnqueueFgaWrite(user, CanView, obj);
            await writer.SaveChangesAsync();
        }

        await WaitForEmptyOutboxAsync(ctx);
        stopwatch.Stop();

        stopwatch.Elapsed.ShouldBeLessThan(UnreachablePollInterval);

        var read = await fga.Client.Read(new ClientReadRequest { Object = obj.AsOpenFgaIdTupleString() });
        read.Tuples.ShouldContain(t =>
            t.Key.User == user.AsOpenFgaIdTupleString() && t.Key.Relation == "can_view"
        );
    }

    /// <summary>Builds and starts a real hosted service whose only viable wake-up is the signal.</summary>
    private DrainerHost StartedDrainer(SealedFgaOutboxSignal signal) {
        var services = new ServiceCollection();
        services.AddScoped(_ => pg.CreateContext());
        services.AddScoped(_ => fga.Service);
        var provider = services.BuildServiceProvider();

        var options = Options.Create(new SealedFgaOptions {
            RunOutboxDrainer = true,
            OutboxPollInterval = UnreachablePollInterval,
        });

        return new DrainerHost(
            new SealedFgaOutboxHostedService<TestDbContext>(provider, options, signal),
            provider
        );
    }

    /// <summary>Polls the outbox until the drainer has processed everything, or the deadline lapses.</summary>
    private static async Task WaitForEmptyOutboxAsync(TestDbContext ctx) {
        using var deadline = new CancellationTokenSource(DrainDeadline);
        while (true) {
            var pending = await ctx.Outbox.CountAsync(e => e.ProcessedAtUtc == null, deadline.Token);
            if (pending == 0) {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
        }
    }

    /// <summary>Owns the hosted service and its provider so both are torn down with the test.</summary>
    private sealed class DrainerHost : IAsyncDisposable {
        private readonly SealedFgaOutboxHostedService<TestDbContext> _service;
        private readonly ServiceProvider _provider;

        public DrainerHost(SealedFgaOutboxHostedService<TestDbContext> service, ServiceProvider provider) {
            _service = service;
            _provider = provider;
            StartedAsync = StartAsync();
        }

        public Task StartedAsync { get; }

        private async Task StartAsync() {
            await _service.StartAsync(CancellationToken.None);
            // The loop drains once before it ever waits; let that pass finish so the assertions
            // below are about the signal rather than about startup timing.
            await Task.Delay(SettleDelay);
        }

        public async ValueTask DisposeAsync() {
            await _service.StopAsync(CancellationToken.None);
            _service.Dispose();
            await _provider.DisposeAsync();
        }
    }
}
