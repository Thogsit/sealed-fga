using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using OpenFga.Sdk.Client;
using SealedFga;
using SealedFga.AuthModel;
using SealedFga.Fga;
using SealedFga.Sample.Secret;
using SealedFga.Sample.User;
using Shouldly;
using Xunit;

namespace SealedFga.IntegrationTests;

[Collection(SampleWebAppCollection.Name)]
[Trait("Category", "Integration")]
public class BatchCheckDispatchIntegrationTests(SampleWebAppFixture fixture) {
    [Fact]
    public async Task Generated_batch_matches_single_checks_with_ambient_and_explicit_tuples() {
        var user = new UserEntityId($"batch-{Guid.NewGuid()}");
        var agency = AgencyEntityId.Parse("b490657a-2c5f-4bab-b540-3753d02aca53");
        const string secret1 = "f6c603cd-881c-4433-ae74-8a0b4e70d67b";
        const string secret3 = "0240d59b-2d04-4391-9700-1902b75385b9";
        using var client = new OpenFgaClient(new ClientConfiguration {
            ApiUrl = fixture.ApiUrl,
            StoreId = fixture.StoreId,
            AuthorizationModelId = fixture.AuthorizationModelId,
        });
        var provider = new ContextualGrantProvider(secret3);
        ISealedFgaService service = new SealedFgaService(client, Options.Create(new SealedFgaOptions()), null, provider);
        var options = new SealedFgaQueryOptions {
            ContextualTuples = [SealedFgaContextualTuple.Of(user, AgencyEntityIdGroups.Member, agency)],
        };
        TypeNameCheck[] checks = [
            new("secret", "can_edit", secret3), // per-object ambient grant
            new("agency", "Member", agency.ToString()), // explicit contextual grant
            new("secret", "can_view", secret3), // denied
            new("secret", "can_view", secret1), // computed through explicit agency membership
        ];

        var actual = await service.TryBatchCheckByTypeNameAsync(user, checks, options);
        actual.ShouldBe([true, true, false, true]);
        provider.Calls.ShouldBe(checks.Length);
        var expected = new List<bool>();
        foreach (var check in checks) {
            expected.Add((await service.TryCheckByTypeNameAsync(user, check.TypeName, check.Relation, check.ObjectId, options))!.Value);
        }
        actual.ShouldBe(expected);
    }

    private sealed class ContextualGrantProvider(string targetId) : ISealedFgaAmbientOptionsProvider {
        public int Calls { get; private set; }

        public ValueTask<SealedFgaQueryOptions?> GetCheckOptionsAsync<TObjId>(
            ISealedFgaUser user,
            ISealedFgaRelation<TObjId> relation,
            TObjId objectId,
            CancellationToken cancellationToken = default
        ) where TObjId : ISealedFgaTypeId<TObjId> {
            Calls++;
            return ValueTask.FromResult<SealedFgaQueryOptions?>(
                objectId.AsOpenFgaIdTupleString() == $"secret:{targetId}"
                    ? new SealedFgaQueryOptions {
                        ContextualTuples = [SealedFgaContextualTuple.Of(
                            user, SecretEntityIdPermissions.can_edit, SecretEntityId.Parse(targetId))],
                    }
                    : null);
        }
    }
}
