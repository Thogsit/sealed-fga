using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     Background worker that periodically drains the SealedFGA outbox into OpenFGA. Registered
///     automatically by the generated <c>ConfigureSealedFga&lt;TDbContext&gt;()</c>.
///     Cluster-safe: on relational providers only the replica holding the drainer lease
///     (<see cref="SealedFgaOutboxLease" />) drains — the lease is renewed ahead of every drain
///     pass and other replicas take over once it lapses. Non-relational providers (InMemory) are
///     single-process by definition and always drain. The leader also runs the periodic
///     retention sweep (<see cref="SealedFgaOptions.OutboxRetentionPeriod" />).
/// </summary>
/// <typeparam name="TDbContext">The consumer's <see cref="DbContext" /> that hosts the outbox table.</typeparam>
public class SealedFgaOutboxHostedService<TDbContext>(
    IServiceProvider serviceProvider,
    IOptions<SealedFgaOptions> options
) : BackgroundService
    where TDbContext : DbContext {
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    /// <summary>This replica's lease identity for the lifetime of the hosted service.</summary>
    private readonly string _holderId = Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var opts = options.Value;

        // Allows consumers to opt out of the built-in drainer (e.g. to drain manually / in tests).
        if (!opts.RunOutboxDrainer) {
            return;
        }

        var timeProvider = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
        var nextSweepUtc = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested) {
            try {
                using var scope = serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<TDbContext>();
                var fgaService = scope.ServiceProvider.GetRequiredService<SealedFgaService>();

                var isLeader = await TryAcquireOrRenewLeaseAsync(context, opts, timeProvider, stoppingToken);
                if (isLeader) {
                    // Drain until the queue is empty (or nothing is currently eligible), renewing
                    // the lease ahead of every pass so a lost lease stops the drain within one pass.
                    int changed;
                    do {
                        changed = await SealedFgaOutboxDrainer.DrainOnceAsync(
                            context,
                            fgaService,
                            opts.OutboxBatchSize,
                            opts.OutboxMaxAttempts,
                            timeProvider,
                            stoppingToken
                        );
                    } while (changed > 0
                             && !stoppingToken.IsCancellationRequested
                             && await TryAcquireOrRenewLeaseAsync(context, opts, timeProvider, stoppingToken));

                    var now = timeProvider.GetUtcNow().UtcDateTime;
                    if (now >= nextSweepUtc
                        && !stoppingToken.IsCancellationRequested
                        && await TryAcquireOrRenewLeaseAsync(context, opts, timeProvider, stoppingToken)) {
                        await SealedFgaOutboxRetentionSweeper.SweepAsync(
                            context,
                            opts.OutboxRetentionPeriod,
                            opts.OutboxMaxAttempts,
                            timeProvider,
                            stoppingToken
                        );
                        nextSweepUtc = now.Add(SweepInterval);
                    }
                }
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                // Graceful shutdown.
                break;
            } catch (Exception) {
                // Never let the loop die on an unexpected error; per-row failures are already handled
                // inside the drainer. Wait for the next poll and try again.
            }

            try {
                await Task.Delay(opts.OutboxPollInterval, timeProvider, stoppingToken);
            } catch (OperationCanceledException) {
                break;
            }
        }
    }

    private async Task<bool> TryAcquireOrRenewLeaseAsync(
        TDbContext context,
        SealedFgaOptions opts,
        TimeProvider timeProvider,
        CancellationToken ct
    ) =>
        // Non-relational providers (InMemory) are single-process; no lease needed (and
        // ExecuteUpdate would throw). Relational replicas compete for the lease row.
        !context.Database.IsRelational()
        || await SealedFgaOutboxLeaseManager.TryAcquireOrRenewAsync(
            context,
            _holderId,
            opts.OutboxLeaseDuration,
            timeProvider,
            ct
        );
}
