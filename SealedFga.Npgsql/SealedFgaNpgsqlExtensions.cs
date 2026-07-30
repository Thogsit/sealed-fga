using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SealedFga.Fga.Outbox;

namespace SealedFga.Npgsql;

/// <summary>
///     Opt-in wiring for the PostgreSQL <c>LISTEN</c>/<c>NOTIFY</c> outbox transport.
/// </summary>
/// <remarks>
///     <para>
///         Why this is needed at all: only the replica holding the drainer lease drains, and
///         leadership is sticky. On a multi-replica deployment most enqueues therefore land on a
///         replica that cannot act on them, while the leader — whose in-process signal nobody raised
///         — sleeps out <see cref="SealedFgaOptions.OutboxPollInterval" />. For that window a caller
///         that just created an entity fails permission checks against it.
///     </para>
///     <para>
///         <b>Requires session-level PostgreSQL connections.</b> A transaction-mode connection pooler
///         (PgBouncer's default, Supavisor's transaction port) breaks <c>LISTEN</c> silently, with no
///         error anywhere: the subscription lives on a server connection that later statements no
///         longer land on. Direct connections and session-mode pooling are both fine. See
///         <see cref="SealedFgaNpgsqlOutboxOptions.ListenerConnectionString" /> for routing the
///         listener around a pooler.
///     </para>
/// </remarks>
public static class SealedFgaNpgsqlExtensions {
    /// <summary>
    ///     Registers both halves of the transport: a notifier that publishes on the consumer's own
    ///     connection, and a background listener that turns incoming notifications into local drain
    ///     wake-ups. Pair it with <see cref="AddSealedFgaNpgsqlNotify" /> on the context, which is
    ///     what actually attaches the publish side to saves.
    /// </summary>
    /// <typeparam name="TDbContext">The consumer's <see cref="DbContext" /> that hosts the outbox table.</typeparam>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configure">Optional channel / connection / keep-alive overrides.</param>
    public static IServiceCollection AddSealedFgaNpgsqlSignaling<TDbContext>(
        this IServiceCollection services,
        Action<SealedFgaNpgsqlOutboxOptions>? configure = null
    )
        where TDbContext : DbContext {
        if (configure is not null) {
            services.Configure(configure);
        }

        // Also TryAdd-ed by the generated ConfigureSealedFga; registering it here as well keeps this
        // call order-independent relative to that one, and both resolve the same instance.
        services.TryAddSingleton<SealedFgaOutboxSignal>();

        services.TryAddSingleton<ISealedFgaOutboxNotifier, SealedFgaNpgsqlOutboxNotifier>();
        services.TryAddScoped<SealedFgaOutboxNotifyInterceptor>();

        // Singleton first, hosted service second, so the health check observes the same instance the
        // host is running rather than a second, never-started copy.
        services.TryAddSingleton<SealedFgaNpgsqlOutboxListener<TDbContext>>();
        services.AddHostedService(sp => sp.GetRequiredService<SealedFgaNpgsqlOutboxListener<TDbContext>>());

        return services;
    }

    /// <summary>
    ///     Attaches the publish side to this context, so a save that enqueues outbox rows also emits
    ///     a <c>NOTIFY</c> on the context's connection.
    ///     <para>
    ///         Separate from <see cref="AddSealedFgaNpgsqlSignaling{TDbContext}" /> because it is the
    ///         half that touches the consumer's write path: it runs a statement on their connection
    ///         inside their <c>SaveChanges</c>, and the transactional guarantees only hold for a
    ///         context that really is the PostgreSQL one holding the outbox.
    ///     </para>
    /// </summary>
    /// <example>
    ///     <code>
    ///     services.AddDbContext&lt;MyDbContext&gt;((sp, options) => {
    ///         options.UseNpgsql(connectionString);
    ///         options.AddSealedFga(sp);
    ///         options.AddSealedFgaNpgsqlNotify(sp);
    ///     });
    ///     services.ConfigureSealedFga&lt;MyDbContext&gt;(configuration);
    ///     services.AddSealedFgaNpgsqlSignaling&lt;MyDbContext&gt;();
    ///     </code>
    /// </example>
    /// <param name="options">The context options being built.</param>
    /// <param name="serviceProvider">The provider the interceptor is resolved from.</param>
    public static DbContextOptionsBuilder AddSealedFgaNpgsqlNotify(
        this DbContextOptionsBuilder options,
        IServiceProvider serviceProvider
    ) {
        // Must come after AddSealedFga: it reports on the outbox rows that call's interceptor adds
        // during the same SavingChanges.
        options.AddInterceptors(serviceProvider.GetRequiredService<SealedFgaOutboxNotifyInterceptor>());
        return options;
    }

    /// <summary>
    ///     Registers the listener health check. Reports degraded — never unhealthy — while the
    ///     subscription is down, so a database blip cannot pull instances out of rotation over what
    ///     is only a latency optimization.
    /// </summary>
    /// <typeparam name="TDbContext">The consumer's <see cref="DbContext" /> that hosts the outbox table.</typeparam>
    /// <param name="builder">The health-checks builder to register into.</param>
    /// <param name="name">The health check's registration name.</param>
    public static IHealthChecksBuilder AddSealedFgaNpgsqlOutboxListenerCheck<TDbContext>(
        this IHealthChecksBuilder builder,
        string name = "sealedfga-outbox-listener"
    )
        where TDbContext : DbContext =>
        builder.AddCheck<SealedFgaNpgsqlOutboxListenerHealthCheck<TDbContext>>(name);
}
