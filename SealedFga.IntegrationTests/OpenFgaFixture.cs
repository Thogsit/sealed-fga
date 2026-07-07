using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using OpenFga.Sdk.Client;
using OpenFga.Sdk.Client.Model;
using OpenFga.Sdk.Model;
using SealedFga;
using SealedFga.Fga;
using SealedFga.Tests.Support;
using SealedFga.Util;
using Xunit;

namespace SealedFga.IntegrationTests;

/// <summary>
///     Spins up an ephemeral OpenFGA container (via Testcontainers), creates a store and writes an
///     authorization model that matches the shared test entities (<c>testuser</c>, <c>testparent</c>,
///     <c>testobject</c>), and exposes a configured <see cref="OpenFgaClient" /> / <see cref="SealedFgaService" />.
/// </summary>
public sealed class OpenFgaFixture : IAsyncLifetime {
    // Direct-relation model mirroring the shared test entities' [SealedFgaRelation]s:
    //   testparent.Member  : [testuser]     (TestUserEntity, TargetType.User)
    //   testobject.OwnedBy  : [testparent]   (TestObjectEntity, TargetType.Object)
    //   testobject.can_view : [testuser]     (a plain checkable relation for the service tests)
    private const string TypeDefinitionsJson =
        """
        [
          { "type": "testuser" },
          {
            "type": "testparent",
            "relations": { "Member": { "this": {} } },
            "metadata": { "relations": { "Member": { "directly_related_user_types": [ { "type": "testuser" } ] } } }
          },
          {
            "type": "testobject",
            "relations": { "OwnedBy": { "this": {} }, "can_view": { "this": {} } },
            "metadata": { "relations": {
              "OwnedBy":  { "directly_related_user_types": [ { "type": "testparent" } ] },
              "can_view": { "directly_related_user_types": [ { "type": "testuser" } ] }
            } }
          }
        ]
        """;

    private readonly IContainer _container = new ContainerBuilder()
                                            .WithImage("openfga/openfga:latest")
                                            .WithCommand("run")
                                            .WithPortBinding(8080, true)
                                            .WithWaitStrategy(
                                                 Wait.ForUnixContainer()
                                                     .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/healthz"))
                                             )
                                            .Build();

    public OpenFgaClient Client { get; private set; } = null!;
    public string ApiUrl { get; private set; } = null!;
    public string StoreId { get; private set; } = null!;
    public string AuthorizationModelId { get; private set; } = null!;
    public SealedFgaService Service => new(Client, Microsoft.Extensions.Options.Options.Create(new SealedFgaOptions()));

    public async Task InitializeAsync() {
        await _container.StartAsync();
        ApiUrl = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(8080)}";

        // Register the shared test IDs with IdUtil (the drainer/service map raw strings back to IDs).
        IdUtil.RegisterIdType(typeof(TestObjectId), TestObjectId.OpenFgaTypeName);
        IdUtil.RegisterIdType(typeof(TestParentId), TestParentId.OpenFgaTypeName);
        IdUtil.RegisterIdType(typeof(TestUserId), TestUserId.OpenFgaTypeName);
        IdUtil.RegisterIdTypeParseMethod(typeof(TestObjectId), s => TestObjectId.Parse(s));
        IdUtil.RegisterIdTypeParseMethod(typeof(TestParentId), s => TestParentId.Parse(s));
        IdUtil.RegisterIdTypeParseMethod(typeof(TestUserId), s => TestUserId.Parse(s));

        var bootstrap = new OpenFgaClient(new ClientConfiguration { ApiUrl = ApiUrl });
        var store = await bootstrap.CreateStore(new ClientCreateStoreRequest { Name = "sealedfga-tests" });
        StoreId = store.Id;

        var storeClient = new OpenFgaClient(new ClientConfiguration { ApiUrl = ApiUrl, StoreId = StoreId });
        var typeDefinitions = JsonSerializer.Deserialize<List<TypeDefinition>>(TypeDefinitionsJson)!;
        var model = await storeClient.WriteAuthorizationModel(
            new ClientWriteAuthorizationModelRequest { SchemaVersion = "1.1", TypeDefinitions = typeDefinitions }
        );
        AuthorizationModelId = model.AuthorizationModelId;

        Client = new OpenFgaClient(new ClientConfiguration {
            ApiUrl = ApiUrl,
            StoreId = StoreId,
            AuthorizationModelId = AuthorizationModelId,
        });
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>Shares one OpenFGA container across every integration-test class in the collection.</summary>
[CollectionDefinition(Name)]
public sealed class OpenFgaCollection : ICollectionFixture<OpenFgaFixture> {
    public const string Name = "openfga";
}
