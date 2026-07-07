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
/// </summary>
/// <typeparam name="TDbContext">The consumer's <see cref="DbContext" /> that hosts the outbox table.</typeparam>
public class SealedFgaOutboxHostedService<TDbContext>(
    IServiceProvider serviceProvider,
    IOptions<SealedFgaOptions> options
) : BackgroundService
    where TDbContext : DbContext {
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var opts = options.Value;

        // Allows consumers to opt out of the built-in drainer (e.g. to drain manually / in tests).
        if (!opts.QueueFgaServiceOperations) {
            return;
        }

        while (!stoppingToken.IsCancellationRequested) {
            try {
                using var scope = serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<TDbContext>();
                var fgaService = scope.ServiceProvider.GetRequiredService<SealedFgaService>();

                // Drain until the queue is empty (or nothing is currently eligible), then idle.
                int processed;
                do {
                    processed = await SealedFgaOutboxDrainer.DrainOnceAsync(
                        context,
                        fgaService,
                        opts.OutboxBatchSize,
                        opts.OutboxMaxAttempts,
                        stoppingToken
                    );
                } while (processed > 0 && !stoppingToken.IsCancellationRequested);
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                // Graceful shutdown.
                break;
            } catch (Exception) {
                // Never let the loop die on an unexpected error; per-row failures are already handled
                // inside the drainer. Wait for the next poll and try again.
            }

            try {
                await Task.Delay(opts.OutboxPollInterval, stoppingToken);
            } catch (OperationCanceledException) {
                break;
            }
        }
    }
}
