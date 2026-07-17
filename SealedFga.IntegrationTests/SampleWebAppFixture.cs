using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenFga.Language;
using OpenFga.Sdk.Client;
using OpenFga.Sdk.Client.Model;
using OpenFga.Sdk.Model;
using SealedFga;
using SealedFga.Fga;
using SealedFga.Fga.Outbox;
using SealedFga.ModelBinder;
using SealedFga.Sample;
using SealedFga.Sample.Database;
using Xunit;

namespace SealedFga.IntegrationTests;

/// <summary>
///     Hosts the real <c>SealedFga.Sample</c> web app in-process (<see cref="WebApplicationFactory{TEntryPoint}" />)
///     against an ephemeral OpenFGA container (Testcontainers). It loads the Sample's real
///     <c>model.fga</c> authorization model, points the app's <see cref="OpenFgaClient" /> at the container,
///     seeds the dummy data, and drains the outbox once so the relationship tuples exist in OpenFGA.
///     Tests then exercise the endpoints over HTTP through the full model-binding + authorization pipeline.
/// </summary>
public sealed class SampleWebAppFixture : IAsyncLifetime {
    private readonly IContainer _container = new ContainerBuilder("openfga/openfga:v1.15.1")
                                            .WithCommand("run")
                                            .WithPortBinding(8080, true)
                                            .WithWaitStrategy(
                                                 Wait.ForUnixContainer()
                                                     .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/healthz"))
                                             )
                                            .Build();

    private WebApplicationFactory<Program> _factory = null!;

    public string ApiUrl { get; private set; } = null!;
    public string StoreId { get; private set; } = null!;
    public string AuthorizationModelId { get; private set; } = null!;

    public HttpClient CreateClient() => _factory.CreateClient();

    public async Task InitializeAsync() {
        await _container.StartAsync();
        ApiUrl = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(8080)}";

        // Create a store and load the Sample's real authorization model (model.fga) into it.
        var bootstrap = new OpenFgaClient(new ClientConfiguration { ApiUrl = ApiUrl });
        var store = await bootstrap.CreateStore(new ClientCreateStoreRequest { Name = "sealedfga-sample-tests" });
        StoreId = store.Id;

        var dsl = await File.ReadAllTextAsync(Path.Combine(System.AppContext.BaseDirectory, "model.fga"));
        var modelJson = OpenFgaFromDslTransformer.Transform(dsl);
        var parsedModel = JsonSerializer.Deserialize<AuthorizationModel>(modelJson)!;

        // The DSL transformer emits a spurious empty `wildcard: {}` on every directly-related type
        // reference (e.g. {"type":"agency","wildcard":{}}). For plain `[type]` assignments this makes
        // OpenFGA reject the model ("relation type 'agency' ... is not valid"), so strip it.
        foreach (var relation in parsedModel.TypeDefinitions
                                            .Where(td => td.Metadata?.Relations is not null)
                                            .SelectMany(td => td.Metadata.Relations!.Values)) {
            foreach (var reference in relation.DirectlyRelatedUserTypes ?? []) {
                reference.Wildcard = null;
            }
        }

        var storeClient = new OpenFgaClient(new ClientConfiguration { ApiUrl = ApiUrl, StoreId = StoreId });
        var model = await storeClient.WriteAuthorizationModel(
            new ClientWriteAuthorizationModelRequest {
                SchemaVersion = parsedModel.SchemaVersion,
                TypeDefinitions = parsedModel.TypeDefinitions,
                Conditions = parsedModel.Conditions,
            }
        );
        AuthorizationModelId = model.AuthorizationModelId;

        var containerClient = new OpenFgaClient(new ClientConfiguration {
            ApiUrl = ApiUrl,
            StoreId = StoreId,
            AuthorizationModelId = AuthorizationModelId,
        });

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
                builder.ConfigureTestServices(services => {
                        // Point OpenFGA at the ephemeral test container instead of the hard-coded localhost:8080.
                        services.RemoveAll<OpenFgaClient>();
                        services.AddSingleton(containerClient);
                        // Drain the outbox deterministically from the test instead of the 5s background poll.
                        services.Configure<SealedFgaOptions>(o => o.QueueFgaServiceOperations = false);
                        // Exercise the binder options hook: grants contextual tuples per request header
                        // (no header -> null -> default binder behavior for all other tests).
                        services.AddSingleton<ISealedFgaBinderOptionsProvider, HeaderContextualTupleProvider>();
                    }
                );
            }
        );

        // Accessing Services starts the host. WebApplicationFactory intercepts Program's post-Build
        // seeding (it never runs), so we seed and drain the outbox ourselves.
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SealedFgaSampleContext>();
        await context.Database.EnsureCreatedAsync();
        if (!await context.SecretEntities.AnyAsync()) {
            await context.AddDummyData();
        }

        var fgaService = scope.ServiceProvider.GetRequiredService<SealedFgaService>();
        await SealedFgaOutboxDrainer.DrainOnceAsync(context, fgaService, batchSize: 100, maxAttempts: 10);
    }

    public async Task DisposeAsync() {
        if (_factory is not null) {
            await _factory.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}

/// <summary>Shares one Sample web host + OpenFGA container across the endpoint tests.</summary>
[CollectionDefinition(Name)]
public sealed class SampleWebAppCollection : ICollectionFixture<SampleWebAppFixture> {
    public const string Name = "sample-web";
}
