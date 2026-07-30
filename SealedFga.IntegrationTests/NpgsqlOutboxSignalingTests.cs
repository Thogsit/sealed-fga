using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenFga.Sdk.Client.Model;
using Npgsql;
using SealedFga.AuthModel;
using SealedFga.Fga;
using SealedFga.Fga.Outbox;
using SealedFga.Npgsql;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.IntegrationTests;

/// <summary>
///     Cover for the cross-replica wake-up path (<c>SealedFga.Npgsql</c>). The process-local signal
///     cannot help a two-replica deployment: only the lease holder drains, so an enqueue on any other
///     replica is invisible to it until the next poll. These tests assert that a
///     <c>LISTEN</c>/<c>NOTIFY</c> round trip closes that gap.
///     <para>
///         Except where a test is explicitly about the poll backstop, <c>OutboxPollInterval</c> is set
///         far beyond the test's own deadline, so <b>only</b> the notify path can make them pass — a
///         regression that silently falls back to polling turns them red rather than slow.
///     </para>
/// </summary>
[Collection(OpenFgaCollection.Name)]
[Trait("Category", "Integration")]
public sealed class NpgsqlOutboxSignalingTests(OpenFgaFixture fga, PostgresFixture pg)
    : IClassFixture<PostgresFixture> {
    /// <summary>Long enough that a poll-driven drain cannot be mistaken for a notify-driven one.</summary>
    private static readonly TimeSpan UnreachablePollInterval = TimeSpan.FromMinutes(5);

    /// <summary>Budget for a notified drain; two orders of magnitude under the poll interval.</summary>
    private static readonly TimeSpan DrainDeadline = TimeSpan.FromSeconds(20);

    /// <summary>How long a "nothing should happen yet" assertion waits before believing it.</summary>
    private static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan PollStep = TimeSpan.FromMilliseconds(100);

    private sealed class TestObjectRelation(string val) : SealedFgaRelation(val), ISealedFgaRelation<TestObjectId>;

    private static readonly TestObjectRelation CanView = new("can_view");

    /// <summary>
    ///     The scenario this package exists for: the enqueue lands on a replica that is not the lease
    ///     holder, so the drainer that has to act on it never saw the local signal. The two replicas
    ///     get genuinely separate containers — and therefore separate
    ///     <see cref="SealedFgaOutboxSignal" /> instances — so the only thing connecting them is
    ///     PostgreSQL.
    /// </summary>
    [Fact]
    public async Task An_enqueue_on_one_replica_wakes_the_drainer_on_another() {
        var channel = NewChannel();
        await using var ctx = await pg.CreateCleanContextAsync();

        await using var enqueuer = await StartReplicaAsync(channel, listen: false, drain: false);
        await using var drainer = await StartReplicaAsync(channel, listen: true, drain: true);

        // Nothing is shared in-process: the drainer's signal is a different object entirely.
        drainer.Signal.ShouldNotBeSameAs(enqueuer.Signal);

        var user = new TestUserId($"alice-{Guid.NewGuid():N}");
        var obj = TestObjectId.New();

        await using (var writer = enqueuer.CreateEnqueueContext()) {
            writer.EnqueueFgaWrite(user, CanView, obj);
            await writer.SaveChangesAsync();
        }

        await WaitForEmptyOutboxAsync(ctx, DrainDeadline);

        await ShouldHaveTupleAsync(user, obj);
    }

    /// <summary>
    ///     Riding the caller's own connection means PostgreSQL holds the notification until commit.
    ///     That is what makes an explicit outer transaction work: waking the drainer at
    ///     <c>SaveChanges</c> would send it looking for rows no other connection can see yet, and it
    ///     would go back to sleep having found nothing.
    /// </summary>
    [Fact]
    public async Task A_notification_inside_a_transaction_is_deferred_until_commit() {
        var channel = NewChannel();
        await using var ctx = await pg.CreateCleanContextAsync();

        await using var enqueuer = await StartReplicaAsync(channel, listen: false, drain: false);
        await using var drainer = await StartReplicaAsync(channel, listen: true, drain: true);
        await using var probe = await NotificationProbe.StartAsync(pg.ConnectionString, channel);

        var user = new TestUserId($"bob-{Guid.NewGuid():N}");
        var obj = TestObjectId.New();

        await using var writer = enqueuer.CreateEnqueueContext();
        await using var transaction = await writer.Database.BeginTransactionAsync();

        writer.EnqueueFgaWrite(user, CanView, obj);
        await writer.SaveChangesAsync();

        // The save is done, but the transaction is not — so neither the row nor the notification
        // exists for anyone else yet.
        await Task.Delay(QuietWindow);
        probe.Count.ShouldBe(0);

        await transaction.CommitAsync();

        await WaitForAsync(() => Task.FromResult(probe.Count > 0), DrainDeadline, "a notification after commit");
        await WaitForEmptyOutboxAsync(ctx, DrainDeadline);

        await ShouldHaveTupleAsync(user, obj);
    }

    /// <summary>
    ///     The flip side of deferral: a rolled-back transaction discards the queued notification with
    ///     everything else, so nobody is woken for rows that no longer exist.
    /// </summary>
    [Fact]
    public async Task A_notification_is_never_delivered_when_the_transaction_rolls_back() {
        var channel = NewChannel();
        await using var ctx = await pg.CreateCleanContextAsync();

        await using var enqueuer = await StartReplicaAsync(channel, listen: false, drain: false);
        await using var probe = await NotificationProbe.StartAsync(pg.ConnectionString, channel);

        var user = new TestUserId($"carol-{Guid.NewGuid():N}");
        var obj = TestObjectId.New();

        await using (var writer = enqueuer.CreateEnqueueContext()) {
            await using var transaction = await writer.Database.BeginTransactionAsync();
            writer.EnqueueFgaWrite(user, CanView, obj);
            await writer.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        await Task.Delay(QuietWindow);

        probe.Count.ShouldBe(0);
        (await ctx.Outbox.CountAsync()).ShouldBe(0);
    }

    /// <summary>
    ///     An idle <c>LISTEN</c> connection is exactly the kind a firewall or pooler reaps, and a
    ///     listener that does not come back degrades to polling without ever logging an error. The
    ///     backend is killed out from under it here; the assertion is deliberately made with an
    ///     enqueue issued <b>after</b> the new subscription exists, so it proves re-subscription
    ///     rather than the forced drain that follows every reconnect.
    /// </summary>
    [Fact]
    public async Task The_listener_resubscribes_after_its_backend_is_terminated() {
        var channel = NewChannel();
        await using var ctx = await pg.CreateCleanContextAsync();

        await using var enqueuer = await StartReplicaAsync(channel, listen: false, drain: false);
        await using var drainer = await StartReplicaAsync(channel, listen: true, drain: true);

        var originalPid = await WaitForListenerPidAsync(null);

        await ExecuteScalarAsync<bool>($"SELECT pg_terminate_backend({originalPid})");

        var newPid = await WaitForListenerPidAsync(originalPid);
        newPid.ShouldNotBe(originalPid);
        drainer.Listener!.IsListening.ShouldBeTrue();

        var user = new TestUserId($"dave-{Guid.NewGuid():N}");
        var obj = TestObjectId.New();

        await using (var writer = enqueuer.CreateEnqueueContext()) {
            writer.EnqueueFgaWrite(user, CanView, obj);
            await writer.SaveChangesAsync();
        }

        await WaitForEmptyOutboxAsync(ctx, DrainDeadline);

        await ShouldHaveTupleAsync(user, obj);
    }

    /// <summary>
    ///     Losing the listener must cost latency and nothing else. With the subscription stopped, the
    ///     only remaining path is the drainer's poll — so this is the one test that gives it a
    ///     reachable interval, and it asserts the work still lands.
    /// </summary>
    [Fact]
    public async Task A_stopped_listener_degrades_to_the_poll_backstop() {
        var channel = NewChannel();
        var reachablePoll = TimeSpan.FromSeconds(2);
        await using var ctx = await pg.CreateCleanContextAsync();

        await using var enqueuer = await StartReplicaAsync(channel, listen: false, drain: false);
        await using var drainer = await StartReplicaAsync(
            channel,
            listen: true,
            drain: true,
            pollInterval: reachablePoll
        );

        await WaitForAsync(
            () => Task.FromResult(drainer.Listener!.IsListening),
            DrainDeadline,
            "the listener to subscribe"
        );

        await drainer.Listener!.StopAsync(CancellationToken.None);
        drainer.Listener.IsListening.ShouldBeFalse();

        var user = new TestUserId($"erin-{Guid.NewGuid():N}");
        var obj = TestObjectId.New();

        await using (var writer = enqueuer.CreateEnqueueContext()) {
            writer.EnqueueFgaWrite(user, CanView, obj);
            await writer.SaveChangesAsync();
        }

        await WaitForEmptyOutboxAsync(ctx, DrainDeadline);

        await ShouldHaveTupleAsync(user, obj);
    }

    /// <summary>
    ///     Each test gets its own channel: the container is shared for the class, and a stray
    ///     notification from a neighbouring test would otherwise be indistinguishable from the one
    ///     under assertion.
    /// </summary>
    private static string NewChannel() => $"sfga_test_{Guid.NewGuid():N}";

    /// <summary>
    ///     Builds one simulated replica: its own container, and therefore its own signal, notifier and
    ///     listener. Wired through the real public entry points so the tests cover the wiring too.
    /// </summary>
    private async Task<Replica> StartReplicaAsync(
        string channel,
        bool listen,
        bool drain,
        TimeSpan? pollInterval = null
    ) {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => pg.CreateContext());
        services.AddScoped(_ => fga.Service);
        services.AddSealedFgaNpgsqlSignaling<TestDbContext>(o => {
                o.Channel = channel;
                // Reconnects must be quick enough to observe inside a test's deadline.
                o.ReconnectMinBackoff = TimeSpan.FromMilliseconds(200);
                o.ReconnectMaxBackoff = TimeSpan.FromSeconds(1);
            }
        );

        var provider = services.BuildServiceProvider();
        var replica = new Replica(provider, pg);

        if (listen) {
            replica.Listener = provider.GetRequiredService<SealedFgaNpgsqlOutboxListener<TestDbContext>>();
            await replica.Listener.StartAsync(CancellationToken.None);
            await WaitForAsync(
                () => Task.FromResult(replica.Listener.IsListening),
                DrainDeadline,
                "the listener to subscribe"
            );
        }

        if (drain) {
            var options = Options.Create(new SealedFgaOptions {
                    RunOutboxDrainer = true,
                    OutboxPollInterval = pollInterval ?? UnreachablePollInterval,
                }
            );
            replica.Drainer = new SealedFgaOutboxHostedService<TestDbContext>(
                provider,
                options,
                provider.GetRequiredService<SealedFgaOutboxSignal>()
            );
            await replica.Drainer.StartAsync(CancellationToken.None);
            // The loop drains once before it ever waits; let that pass finish so the assertions are
            // about the notification rather than about startup timing.
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return replica;
    }

    /// <summary>Asserts the tuple actually reached OpenFGA, not merely that the outbox row cleared.</summary>
    private async Task ShouldHaveTupleAsync(TestUserId user, TestObjectId obj) {
        var read = await fga.Client.Read(new ClientReadRequest { Object = obj.AsOpenFgaIdTupleString() });
        read.Tuples.ShouldContain(t =>
            t.Key.User == user.AsOpenFgaIdTupleString() && t.Key.Relation == "can_view"
        );
    }

    /// <summary>Polls the outbox until the drainer has processed everything, or the deadline lapses.</summary>
    private static Task WaitForEmptyOutboxAsync(TestDbContext ctx, TimeSpan timeout) =>
        WaitForAsync(
            async () => await ctx.Outbox.CountAsync(e => e.ProcessedAtUtc == null) == 0,
            timeout,
            "the outbox to drain"
        );

    private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout, string what) {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline) {
            if (await condition()) {
                return;
            }

            await Task.Delay(PollStep);
        }

        throw new TimeoutException($"Timed out after {timeout.TotalSeconds:F0}s waiting for {what}.");
    }

    /// <summary>
    ///     Finds the listener's backend by the application name it stamps on its connection, ignoring
    ///     <paramref name="excludedPid" /> so a reconnect can be told apart from the old session.
    /// </summary>
    private async Task<int> WaitForListenerPidAsync(int? excludedPid) {
        int? found = null;
        await WaitForAsync(
            async () => {
                found = await ExecuteScalarAsync<int?>(
                    "SELECT pid FROM pg_stat_activity "
                    + "WHERE application_name = 'sealedfga-outbox-listener' "
                    + $"AND pid <> {excludedPid ?? -1} LIMIT 1"
                );
                return found is not null;
            },
            DrainDeadline,
            excludedPid is null ? "the listener's backend to appear" : "the listener to reconnect on a new backend"
        );

        return found!.Value;
    }

    private async Task<T?> ExecuteScalarAsync<T>(string sql) {
        await using var connection = new NpgsqlConnection(pg.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T) result;
    }

    /// <summary>One simulated replica: a container of its own, plus whatever of it was started.</summary>
    private sealed class Replica(ServiceProvider provider, PostgresFixture pg) : IAsyncDisposable {
        public SealedFgaOutboxSignal Signal { get; } = provider.GetRequiredService<SealedFgaOutboxSignal>();

        public SealedFgaNpgsqlOutboxListener<TestDbContext>? Listener { get; set; }

        public SealedFgaOutboxHostedService<TestDbContext>? Drainer { get; set; }

        /// <summary>
        ///     A context wired the way this replica's consumer code would be: both the local signal
        ///     interceptor the generator always adds and the notify interceptor the consumer opts into.
        /// </summary>
        public TestDbContext CreateEnqueueContext() =>
            pg.CreateContextWithInterceptors(
                new SealedFgaOutboxSignalInterceptor(Signal),
                provider.GetRequiredService<SealedFgaOutboxNotifyInterceptor>()
            );

        public async ValueTask DisposeAsync() {
            if (Drainer is not null) {
                await Drainer.StopAsync(CancellationToken.None);
                Drainer.Dispose();
            }

            if (Listener is not null) {
                await Listener.StopAsync(CancellationToken.None);
                Listener.Dispose();
            }

            await provider.DisposeAsync();
        }
    }

    /// <summary>
    ///     A bare <c>LISTEN</c> connection that only counts what arrives. Lets the transactional tests
    ///     assert on delivery itself rather than on its downstream effect — "the drainer found
    ///     nothing" would be vacuously true for a rollback.
    /// </summary>
    private sealed class NotificationProbe : IAsyncDisposable {
        private readonly NpgsqlConnection _connection;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pump;
        private int _count;

        private NotificationProbe(NpgsqlConnection connection) {
            _connection = connection;
            _connection.Notification += (_, _) => Interlocked.Increment(ref _count);
            _pump = PumpAsync();
        }

        public int Count => Volatile.Read(ref _count);

        public static async Task<NotificationProbe> StartAsync(string connectionString, string channel) {
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var command = new NpgsqlCommand($"""LISTEN "{channel}" """, connection)) {
                await command.ExecuteNonQueryAsync();
            }

            return new NotificationProbe(connection);
        }

        private async Task PumpAsync() {
            try {
                while (!_cts.IsCancellationRequested) {
                    await _connection.WaitAsync(_cts.Token);
                }
            } catch (Exception) {
                // Shutdown, or the connection went away with the test; either way there is nothing
                // left to count.
            }
        }

        public async ValueTask DisposeAsync() {
            await _cts.CancelAsync();
            await _pump;
            await _connection.DisposeAsync();
            _cts.Dispose();
        }
    }
}
