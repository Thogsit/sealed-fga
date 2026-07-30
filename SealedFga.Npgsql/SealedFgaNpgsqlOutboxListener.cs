using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using SealedFga.Fga.Outbox;

namespace SealedFga.Npgsql;

/// <summary>
///     Subscribe side of the cross-replica wake-up: holds one dedicated PostgreSQL connection parked
///     on <c>LISTEN</c> and raises the local <see cref="SealedFgaOutboxSignal" /> for every
///     notification, so a drain starts within milliseconds of another replica's commit.
///     <para>
///         The drainer itself needs no knowledge of any of this — it already wakes on the signal, and
///         this only widens the set of things that can raise it.
///     </para>
///     <para>
///         The connection is deliberately outside EF's pool (<c>Pooling=false</c> on a connection
///         string of its own): it is parked for the lifetime of the process, and a pool slot occupied
///         forever is a slot the request path cannot use.
///     </para>
/// </summary>
/// <typeparam name="TDbContext">The consumer's <see cref="DbContext" />, used to default the connection string.</typeparam>
public class SealedFgaNpgsqlOutboxListener<TDbContext>(
    IServiceProvider serviceProvider,
    IOptions<SealedFgaNpgsqlOutboxOptions> options,
    SealedFgaOutboxSignal signal,
    ILogger<SealedFgaNpgsqlOutboxListener<TDbContext>> logger
) : BackgroundService
    where TDbContext : DbContext {
    private TimeSpan _backoff;

    /// <summary>
    ///     Whether the listener currently holds a live subscription. False means wake-ups from other
    ///     replicas are being missed and only <see cref="SealedFgaOptions.OutboxPollInterval" />
    ///     bounds the delay — degraded, not broken.
    /// </summary>
    public bool IsListening { get; private set; }

    /// <summary>Why the last subscription ended, for the health check to surface.</summary>
    public string? LastError { get; private set; }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var opts = options.Value;
        _backoff = opts.ReconnectMinBackoff;

        string connectionString;
        try {
            connectionString = BuildConnectionString(opts);
        } catch (Exception ex) {
            // Nothing a retry would fix (no connection string to be had), and throwing out of
            // ExecuteAsync would take the host down over a latency optimization.
            LastError = ex.Message;
            logger.LogError(
                ex,
                "SealedFGA outbox listener could not determine a connection string and will not "
                + "start; outbox rows enqueued on other replicas will only be drained on the poll "
                + "interval."
            );
            return;
        }

        while (!stoppingToken.IsCancellationRequested) {
            try {
                await ListenAsync(connectionString, opts.Channel, stoppingToken);
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                break;
            } catch (Exception ex) {
                LastError = ex.Message;
                logger.LogWarning(
                    ex,
                    "SealedFGA outbox listener lost its PostgreSQL subscription on channel "
                    + "{Channel}; retrying in {BackoffSeconds}s. Draining falls back to the poll "
                    + "interval until it is restored.",
                    opts.Channel,
                    _backoff.TotalSeconds
                );
            } finally {
                IsListening = false;
            }

            try {
                await Task.Delay(_backoff, stoppingToken);
            } catch (OperationCanceledException) {
                break;
            }

            var doubled = _backoff.Ticks * 2;
            _backoff = TimeSpan.FromTicks(Math.Min(doubled, opts.ReconnectMaxBackoff.Ticks));
        }
    }

    /// <summary>Opens a subscription and stays on it until the connection dies or shutdown begins.</summary>
    private async Task ListenAsync(string connectionString, string channel, CancellationToken stoppingToken) {
        await using var connection = new NpgsqlConnection(connectionString);
        connection.Notification += OnNotification;

        await connection.OpenAsync(stoppingToken);

        // The channel is an identifier here, not a value, so it cannot be parameterized. Quoted to
        // preserve case (an unquoted LISTEN folds to lower case, pg_notify does not), which is safe
        // because SealedFgaNpgsqlOutboxOptions.Channel forbids quotes.
        await using (var command = new NpgsqlCommand($"""LISTEN "{channel}" """, connection)) {
            await command.ExecuteNonQueryAsync(stoppingToken);
        }

        IsListening = true;
        LastError = null;
        _backoff = options.Value.ReconnectMinBackoff;
        logger.LogInformation(
            "SealedFGA outbox listener subscribed to PostgreSQL channel {Channel}.",
            channel
        );

        // Anything notified while we were away is gone — notifications are not queued for absent
        // listeners. One unconditional drain closes that gap, both on first start and after every
        // reconnect; it is also what makes a flapping connection merely wasteful rather than lossy.
        signal.Signal();

        while (!stoppingToken.IsCancellationRequested) {
            await connection.WaitAsync(stoppingToken);
        }
    }

    /// <summary>
    ///     The payload is deliberately empty and ignored: this is a wake-up, not a message queue.
    ///     Whatever rows exist are discovered by the drain pass itself, which also means duplicate or
    ///     coalesced notifications cost nothing.
    /// </summary>
    private void OnNotification(object sender, NpgsqlNotificationEventArgs args) => signal.Signal();

    /// <summary>
    ///     Derives the listener's connection string from the consumer's, adjusted for a connection
    ///     that is opened once and never returned.
    /// </summary>
    private string BuildConnectionString(SealedFgaNpgsqlOutboxOptions opts) {
        var source = opts.ListenerConnectionString ?? ResolveContextConnectionString();

        return new NpgsqlConnectionStringBuilder(source) {
            // Dedicated and long-lived: pooling it would only park a slot the request path wants.
            Pooling = false,
            // Multiplexing spreads statements across physical connections, so the session holding
            // the subscription stops being the session that receives on it. Force it off rather than
            // inheriting a consumer's setting and failing silently.
            Multiplexing = false,
            KeepAlive = (int) opts.KeepAlive.TotalSeconds,
            TcpKeepAlive = true,
            // Makes the parked connection identifiable in pg_stat_activity.
            ApplicationName = "sealedfga-outbox-listener",
        }.ConnectionString;
    }

    private string ResolveContextConnectionString() {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TDbContext>();
        return context.Database.GetConnectionString()
               ?? throw new InvalidOperationException(
                   $"{typeof(TDbContext).Name} exposes no connection string, so the SealedFGA outbox "
                   + $"listener cannot derive one. Set {nameof(SealedFgaNpgsqlOutboxOptions)}."
                   + $"{nameof(SealedFgaNpgsqlOutboxOptions.ListenerConnectionString)} explicitly."
               );
    }
}
