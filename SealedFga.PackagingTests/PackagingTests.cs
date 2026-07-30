using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenFga.Sdk.Client;
using SealedFga;
using SealedFga.Fga.Outbox;
using SealedFga.Npgsql;
using Shouldly;
using Xunit;

namespace SealedFga.PackagingTests;

/// <summary>
///     Consumes SealedFga <b>as the packed NuGet package</b> (analyzer + runtime, as shipped) — the
///     faithful reproduction of a real consumer. Its purpose is to prove the package works from one
///     <c>PackageReference</c> alone: the generator loads and runs from <c>analyzers/dotnet/cs</c>,
///     the build props kick in, and the runtime library's EF wiring builds the consumer's model.
/// </summary>
public class PackagingTests {
    private static ServiceProvider BuildProvider() {
        var services = new ServiceCollection();

        // A dummy OpenFGA client: required by DI wiring, but never contacted (the drainer is disabled
        // and no service call is made — SaveChanges only runs the interceptor + processor).
        services.AddSingleton(new OpenFgaClient(new ClientConfiguration { ApiUrl = "http://127.0.0.1:1" }));

        services.AddDbContext<ConsumerContext>((sp, options) => {
            options.UseInMemoryDatabase(Guid.NewGuid().ToString());
            options.AddSealedFga(sp); // generated: interceptor + outbox model customizer
        });
        services.ConfigureSealedFga<ConsumerContext>(o => o.RunOutboxDrainer = false);

        return services.BuildServiceProvider();
    }

    /// <summary>
    ///     The same consumer, plus the opt-in PostgreSQL signaling wiring. Still on InMemory: this
    ///     project is about what the packages expose, not about PostgreSQL behavior — that is covered
    ///     against a real database in SealedFga.IntegrationTests.
    /// </summary>
    private static ServiceProvider BuildProviderWithNpgsqlSignaling() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new OpenFgaClient(new ClientConfiguration { ApiUrl = "http://127.0.0.1:1" }));

        services.AddDbContext<ConsumerContext>((sp, options) => {
            options.UseInMemoryDatabase(Guid.NewGuid().ToString());
            options.AddSealedFga(sp);
            options.AddSealedFgaNpgsqlNotify(sp);
        });
        services.ConfigureSealedFga<ConsumerContext>(o => o.RunOutboxDrainer = false);
        services.AddSealedFgaNpgsqlSignaling<ConsumerContext>(o => o.Channel = "sealedfga_packaging_test");

        return services.BuildServiceProvider();
    }

    [Fact]
    public void Packaged_library_builds_the_consumer_model() {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ConsumerContext>();

        // Forcing model creation runs the runtime library's ConfigureSealedFgaOutbox + the generated
        // value-converter registration.
        Should.NotThrow(() => ctx.Model.GetEntityTypes().Any());

        // The outbox entity must have been added automatically by the model customizer.
        ctx.Model.FindEntityType(typeof(SealedFgaOutboxEntry)).ShouldNotBeNull();
    }

    /// <summary>
    ///     The companion package has to stand on its own from one <c>PackageReference</c>: its wiring
    ///     extensions must be reachable, and its Npgsql dependency must arrive with it rather than
    ///     being something the consumer has to add. Resolving the listener forces that assembly to
    ///     load, which is what actually proves the dependency shipped.
    /// </summary>
    [Fact]
    public void Packaged_npgsql_companion_wires_up_from_a_package_reference() {
        using var provider = BuildProviderWithNpgsqlSignaling();

        provider.GetRequiredService<ISealedFgaOutboxNotifier>()
                .ShouldBeOfType<SealedFgaNpgsqlOutboxNotifier>();
        provider.GetRequiredService<SealedFgaNpgsqlOutboxListener<ConsumerContext>>().ShouldNotBeNull();
    }

    /// <summary>
    ///     The notifier is best-effort by contract, and this is the cheapest way to hold it to that:
    ///     on a provider that cannot run <c>pg_notify</c> at all, the consumer's <c>SaveChanges</c>
    ///     must still succeed and still produce its outbox rows. A notifier that let its failure
    ///     escape would take down every write instead of merely costing latency.
    /// </summary>
    [Fact]
    public void Packaged_npgsql_notifier_never_fails_the_consumers_save() {
        using var provider = BuildProviderWithNpgsqlSignaling();
        using var scope = provider.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ConsumerContext>();

        ctx.Widgets.Add(new WidgetEntity { Id = WidgetEntityId.New(), OwnerId = OwnerEntityId.New() });

        Should.NotThrow(() => ctx.SaveChanges());
        ctx.Set<SealedFgaOutboxEntry>().ShouldNotBeEmpty();
    }

    [Fact]
    public void Packaged_library_produces_outbox_rows_on_savechanges() {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ConsumerContext>();

        var widget = new WidgetEntity { Id = WidgetEntityId.New(), OwnerId = OwnerEntityId.New() };
        ctx.Widgets.Add(widget);

        // SaveChanges fires the generated interceptor → the runtime processor reads the change
        // tracker and enqueues outbox rows.
        ctx.SaveChanges();

        var outbox = ctx.Set<SealedFgaOutboxEntry>().ToList();
        outbox.ShouldContain(e =>
            e.OperationType == SealedFgaOutboxOperationType.WriteTuple
            && e.TupleObject == widget.Id.AsOpenFgaIdTupleString()
            && e.TupleRelation == "OwnedBy");
    }
}
