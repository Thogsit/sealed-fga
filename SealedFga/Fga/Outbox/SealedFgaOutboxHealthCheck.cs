using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace SealedFga.Fga.Outbox;

/// <summary>
///     Optional <see cref="IHealthCheck" /> adapter over <see cref="SealedFgaOutboxStats" />:
///     parked (permanently failed) rows ⇒ the registration's failure status (unhealthy by
///     default — DB and OpenFGA may have diverged); oldest-pending age beyond
///     <see cref="SealedFgaOptions.OutboxHealthDegradedPendingAge" /> ⇒ degraded.
///     Not registered automatically — consumers opt in:
///     <c>services.AddHealthChecks().AddCheck&lt;SealedFgaOutboxHealthCheck&lt;MyDbContext&gt;&gt;("sealedfga-outbox")</c>.
///     In-process only; whether/how anything is exposed over HTTP is the consumer's decision.
/// </summary>
/// <typeparam name="TDbContext">The consumer's <see cref="DbContext" /> that hosts the outbox table.</typeparam>
public class SealedFgaOutboxHealthCheck<TDbContext>(
    IServiceProvider serviceProvider,
    IOptions<SealedFgaOptions> options
) : IHealthCheck
    where TDbContext : DbContext {
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    ) {
        var opts = options.Value;
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var timeProvider = scope.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System;

        var stats = await dbContext.GetSealedFgaOutboxStatsAsync(
            opts.OutboxMaxAttempts,
            timeProvider,
            cancellationToken
        );

        var data = new Dictionary<string, object> {
            ["pendingCount"] = stats.PendingCount,
            ["parkedCount"] = stats.ParkedCount,
            ["oldestPendingAgeSeconds"] = stats.OldestPendingAge?.TotalSeconds ?? 0d,
        };

        if (stats.ParkedCount > 0) {
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                $"{stats.ParkedCount} outbox row(s) permanently failed (parked); "
                + "the database and OpenFGA may have diverged — inspect their LastError.",
                data: data
            );
        }

        if (opts.OutboxHealthDegradedPendingAge is { } threshold
            && stats.OldestPendingAge is { } oldestAge
            && oldestAge > threshold) {
            return HealthCheckResult.Degraded(
                $"Oldest pending outbox row is {oldestAge.TotalSeconds:F0}s old "
                + $"(threshold {threshold.TotalSeconds:F0}s) — the backlog is not draining.",
                data: data
            );
        }

        return HealthCheckResult.Healthy(
            $"{stats.PendingCount} pending outbox row(s), none parked.",
            data
        );
    }
}
