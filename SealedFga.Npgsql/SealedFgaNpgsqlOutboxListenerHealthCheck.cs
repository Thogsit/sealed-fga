using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SealedFga.Npgsql;

/// <summary>
///     Reports whether the cross-replica wake-up path is live. Worth registering because its failure
///     mode is otherwise invisible: a dead listener does not break anything, it just quietly returns
///     the deployment to poll-interval latency, and the only symptom is users occasionally failing a
///     permission check right after a write.
///     <para>
///         A down listener is <b>degraded, never unhealthy</b> — the drainer's poll is the
///         correctness backstop, so this must not take an instance out of rotation.
///     </para>
///     Registered by <c>AddSealedFgaNpgsqlOutboxListenerCheck</c>, or directly:
///     <c>services.AddHealthChecks().AddCheck&lt;SealedFgaNpgsqlOutboxListenerHealthCheck&lt;MyDbContext&gt;&gt;("sealedfga-outbox-listener")</c>.
/// </summary>
/// <typeparam name="TDbContext">The consumer's <see cref="DbContext" /> that hosts the outbox table.</typeparam>
public class SealedFgaNpgsqlOutboxListenerHealthCheck<TDbContext>(
    SealedFgaNpgsqlOutboxListener<TDbContext> listener
) : IHealthCheck
    where TDbContext : DbContext {
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    ) {
        var data = new Dictionary<string, object> { ["listening"] = listener.IsListening };
        if (listener.LastError is { } lastError) {
            data["lastError"] = lastError;
        }

        return Task.FromResult(
            listener.IsListening
                ? HealthCheckResult.Healthy("Subscribed to the outbox NOTIFY channel.", data)
                : HealthCheckResult.Degraded(
                    "Not subscribed to the outbox NOTIFY channel; outbox rows enqueued on other "
                    + "replicas are only drained on the poll interval.",
                    data: data
                )
        );
    }
}
