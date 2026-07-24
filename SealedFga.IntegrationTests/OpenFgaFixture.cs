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
    // Model mirroring the shared test entities' [SealedFgaRelation]s:
    //   testparent.Member  : [testuser]     (TestUserEntity, TargetType.User)
    //   testobject.OwnedBy  : [testparent]   (TestObjectEntity, TargetType.Object)
    //   testobject.can_view : [testuser, testparent#Member] (a plain checkable relation for the
    //     service tests; also accepts userset subjects for the typed-enqueue tests)
    //   testobject.can_edit : [testuser] or Member from OwnedBy — a UNION relation (like the
    //     sample model's can_view) where direct grants and computed grants coexist; used to prove
    //     writes materialize stored tuples even when a computed arm already grants access.
    //   testobject.ShareGrant : [testgrant] — a link relation carrying the tuple-source grant
    //     entity (TestGrantEntity) on the tuple's USER side, for the state-machine round-trip.
    private const string TypeDefinitionsJson =
        """
        [
          { "type": "testuser" },
          { "type": "testgrant" },
          {
            "type": "testparent",
            "relations": { "Member": { "this": {} } },
            "metadata": { "relations": { "Member": { "directly_related_user_types": [ { "type": "testuser" } ] } } }
          },
          {
            "type": "testobject",
            "relations": {
              "OwnedBy": { "this": {} },
              "ShareGrant": { "this": {} },
              "can_view": { "this": {} },
              "can_edit": { "union": { "child": [
                { "this": {} },
                { "tupleToUserset": {
                  "tupleset": { "relation": "OwnedBy" },
                  "computedUserset": { "relation": "Member" }
                } }
              ] } }
            },
            "metadata": { "relations": {
              "OwnedBy":  { "directly_related_user_types": [ { "type": "testparent" } ] },
              "ShareGrant": { "directly_related_user_types": [ { "type": "testgrant" } ] },
              "can_view": { "directly_related_user_types": [ { "type": "testuser" }, { "type": "testparent", "relation": "Member" } ] },
              "can_edit": { "directly_related_user_types": [ { "type": "testuser" } ] }
            } }
          }
        ]
        """;

    private readonly IContainer _container = new ContainerBuilder("openfga/openfga:v1.15.1")
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
        IdUtil.RegisterIdType(typeof(TestGrantId), TestGrantId.OpenFgaTypeName);
        IdUtil.RegisterIdTypeParseMethod(typeof(TestObjectId), s => TestObjectId.Parse(s));
        IdUtil.RegisterIdTypeParseMethod(typeof(TestParentId), s => TestParentId.Parse(s));
        IdUtil.RegisterIdTypeParseMethod(typeof(TestUserId), s => TestUserId.Parse(s));
        IdUtil.RegisterIdTypeParseMethod(typeof(TestGrantId), s => TestGrantId.Parse(s));

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
