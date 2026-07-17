using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using SealedFga.Fga.Outbox;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>The in-process outbox stats query and the optional health-check adapter.</summary>
public class OutboxStatsAndHealthCheckTests {
    private const int MaxAttempts = 5;

    [Fact]
    public async Task Stats_report_pending_parked_and_oldest_pending_age() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;
        var time = new FakeTimeProvider();
        var now = time.GetUtcNow().UtcDateTime;

        var oldPending = SealedFgaOutboxEntry.ForWrite("u:1", "r", "o:1");
        oldPending.CreatedAtUtc = now.AddMinutes(-10);
        var freshPending = SealedFgaOutboxEntry.ForWrite("u:2", "r", "o:2");
        freshPending.CreatedAtUtc = now;
        var parked = SealedFgaOutboxEntry.ForDelete("u:3", "r", "o:3");
        parked.CreatedAtUtc = now.AddHours(-2);
        parked.Attempts = MaxAttempts;
        var processed = SealedFgaOutboxEntry.ForWrite("u:4", "r", "o:4");
        processed.ProcessedAtUtc = now;
        ctx.Outbox.AddRange(oldPending, freshPending, parked, processed);
        await ctx.SaveChangesAsync();

        var stats = await ctx.GetSealedFgaOutboxStatsAsync(MaxAttempts, time);

        stats.PendingCount.ShouldBe(2);
        stats.ParkedCount.ShouldBe(1);
        // Age tracks the oldest RETRYABLE row; the (older) parked row is the parked alert's job.
        stats.OldestPendingAge.ShouldBe(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task Stats_on_empty_outbox_are_zero_with_no_age() {
        var (ctx, conn) = TestDbContext.CreateSqlite();
        using var _ = conn;

        var stats = await ctx.GetSealedFgaOutboxStatsAsync(MaxAttempts);

        stats.PendingCount.ShouldBe(0);
        stats.ParkedCount.ShouldBe(0);
        stats.OldestPendingAge.ShouldBeNull();
    }

    [Fact]
    public async Task Stats_work_on_the_inmemory_provider() {
        using var ctx = TestDbContext.CreateInMemory();
        ctx.Outbox.Add(SealedFgaOutboxEntry.ForWrite("u:1", "r", "o:1"));
        await ctx.SaveChangesAsync();

        var stats = await ctx.GetSealedFgaOutboxStatsAsync(MaxAttempts);

        stats.PendingCount.ShouldBe(1);
        stats.ParkedCount.ShouldBe(0);
        stats.OldestPendingAge.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(false, false, HealthStatus.Healthy)]
    [InlineData(true, false, HealthStatus.Degraded)]
    [InlineData(false, true, HealthStatus.Unhealthy)]
    [InlineData(true, true, HealthStatus.Unhealthy)] // parked wins over degraded
    public async Task HealthCheck_maps_backlog_state_to_status(
        bool oldBacklog, bool hasParked, HealthStatus expected
    ) {
        var connection = new SqliteConnection("DataSource=:memory:");
        await using var _ = connection;
        connection.Open();
        var time = new FakeTimeProvider();
        var now = time.GetUtcNow().UtcDateTime;

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddDbContext<TestDbContext>(o => o.UseSqlite(connection));
        services.AddOptions<SealedFgaOptions>().Configure(o => {
            o.OutboxMaxAttempts = MaxAttempts;
            o.OutboxHealthDegradedPendingAge = TimeSpan.FromMinutes(5);
        });
        await using var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope()) {
            var ctx = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            ctx.Database.EnsureCreated();
            if (oldBacklog) {
                var stale = SealedFgaOutboxEntry.ForWrite("u:1", "r", "o:1");
                stale.CreatedAtUtc = now.AddMinutes(-30);
                ctx.Outbox.Add(stale);
            }

            if (hasParked) {
                var parked = SealedFgaOutboxEntry.ForWrite("u:2", "r", "o:2");
                parked.Attempts = MaxAttempts;
                ctx.Outbox.Add(parked);
            }

            await ctx.SaveChangesAsync();
        }

        var check = ActivatorUtilities.CreateInstance<SealedFgaOutboxHealthCheck<TestDbContext>>(provider);
        var result = await check.CheckHealthAsync(new HealthCheckContext {
            Registration = new HealthCheckRegistration("sealedfga-outbox", check, HealthStatus.Unhealthy, null),
        });

        result.Status.ShouldBe(expected);
        result.Data["pendingCount"].ShouldBe(oldBacklog ? 1 : 0);
        result.Data["parkedCount"].ShouldBe(hasParked ? 1 : 0);
    }

    [Fact]
    public async Task HealthCheck_age_threshold_can_be_disabled() {
        var connection = new SqliteConnection("DataSource=:memory:");
        await using var _ = connection;
        connection.Open();
        var time = new FakeTimeProvider();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddDbContext<TestDbContext>(o => o.UseSqlite(connection));
        services.AddOptions<SealedFgaOptions>().Configure(o => {
            o.OutboxMaxAttempts = MaxAttempts;
            o.OutboxHealthDegradedPendingAge = null;
        });
        await using var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope()) {
            var ctx = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            ctx.Database.EnsureCreated();
            var stale = SealedFgaOutboxEntry.ForWrite("u:1", "r", "o:1");
            stale.CreatedAtUtc = time.GetUtcNow().UtcDateTime.AddHours(-6);
            ctx.Outbox.Add(stale);
            await ctx.SaveChangesAsync();
        }

        var check = ActivatorUtilities.CreateInstance<SealedFgaOutboxHealthCheck<TestDbContext>>(provider);
        var result = await check.CheckHealthAsync(new HealthCheckContext {
            Registration = new HealthCheckRegistration("sealedfga-outbox", check, HealthStatus.Unhealthy, null),
        });

        result.Status.ShouldBe(HealthStatus.Healthy);
    }
}
