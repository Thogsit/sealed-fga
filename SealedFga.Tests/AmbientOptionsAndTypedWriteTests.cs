using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenFga.Sdk.Client;
using OpenFga.Sdk.Model;
using SealedFga;
using SealedFga.AuthModel;
using SealedFga.Fga;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>
///     Unit tests (via InternalsVisibleTo) for the two additive service features: the ambient
///     options merge semantics behind <see cref="ISealedFgaAmbientOptionsProvider" />, the DI
///     null-injection of the optional provider, and the typed <see cref="SealedFgaTupleOperation" />
///     → <c>TupleKey</c> projection behind the typed write/delete overloads.
/// </summary>
public class AmbientOptionsAndTypedWriteTests {
    private sealed class TestObjectRelation(string val) : SealedFgaRelation(val), ISealedFgaRelation<TestObjectId>;

    private sealed class TestParentRelation(string val) : SealedFgaRelation(val), ISealedFgaRelation<TestParentId>;

    private static readonly TestObjectRelation CanView = new("can_view");
    private static readonly TestParentRelation Member = new("Member");

    #region Merge semantics

    [Fact]
    public void Merge_unions_contextual_tuples_from_both_sides() {
        var user = TestUserId.Parse("alice");
        var ambientObj = TestObjectId.New();
        var perCallObj = TestObjectId.New();
        var ambient = new SealedFgaQueryOptions {
            ContextualTuples = [SealedFgaContextualTuple.Of(user, CanView, ambientObj)],
        };
        var perCall = new SealedFgaQueryOptions {
            ContextualTuples = [SealedFgaContextualTuple.Of(user, CanView, perCallObj)],
        };

        var merged = SealedFgaQueryOptions.Merge(ambient, perCall).ShouldNotBeNull();

        var keys = merged.ToClientTupleKeys().ShouldNotBeNull();
        keys.Count.ShouldBe(2);
        keys.Select(k => k.Object)
            .ShouldBe([ambientObj.AsOpenFgaIdTupleString(), perCallObj.AsOpenFgaIdTupleString()], ignoreOrder: true);
    }

    [Fact]
    public void Merge_lets_explicit_per_call_consistency_win() {
        var ambient = new SealedFgaQueryOptions { Consistency = ConsistencyPreference.MINIMIZELATENCY };
        var perCall = new SealedFgaQueryOptions { Consistency = ConsistencyPreference.HIGHERCONSISTENCY };

        SealedFgaQueryOptions.Merge(ambient, perCall)!.Consistency.ShouldBe(ConsistencyPreference.HIGHERCONSISTENCY);
    }

    [Fact]
    public void Merge_falls_back_to_ambient_consistency_when_per_call_has_none() {
        var ambient = new SealedFgaQueryOptions { Consistency = ConsistencyPreference.HIGHERCONSISTENCY };
        var perCall = new SealedFgaQueryOptions();

        SealedFgaQueryOptions.Merge(ambient, perCall)!.Consistency.ShouldBe(ConsistencyPreference.HIGHERCONSISTENCY);
    }

    [Fact]
    public void Merge_returns_the_other_side_unchanged_when_one_is_null() {
        var only = new SealedFgaQueryOptions { Consistency = ConsistencyPreference.HIGHERCONSISTENCY };

        SealedFgaQueryOptions.Merge(only, null).ShouldBeSameAs(only);
        SealedFgaQueryOptions.Merge(null, only).ShouldBeSameAs(only);
    }

    [Fact]
    public void Merge_of_two_nulls_is_null() =>
        SealedFgaQueryOptions.Merge(null, null).ShouldBeNull();

    #endregion

    #region DI null-injection

    [Fact]
    public void Container_injects_null_ambient_provider_when_none_is_registered() {
        using var provider = BuildContainer(registerAmbientProvider: false);

        var service = provider.GetRequiredService<SealedFgaService>();

        AmbientProviderFieldOf(service).ShouldBeNull();
    }

    [Fact]
    public void Container_injects_the_registered_ambient_provider() {
        var stub = new StubAmbientProvider();
        using var provider = BuildContainer(registerAmbientProvider: true, stub);

        var service = provider.GetRequiredService<SealedFgaService>();

        AmbientProviderFieldOf(service).ShouldBeSameAs(stub);
    }

    private static ServiceProvider BuildContainer(
        bool registerAmbientProvider,
        ISealedFgaAmbientOptionsProvider? stub = null
    ) {
        var services = new ServiceCollection();
        // The real ConfigureSealedFga registers these; construction never contacts the server.
        services.AddSingleton(new OpenFgaClient(new ClientConfiguration {
            ApiUrl = "http://localhost:8080",
            StoreId = "01ARZ3NDEKTSV4RRFFQ69G5FAV",
        }));
        services.AddOptions<SealedFgaOptions>();
        services.AddScoped<SealedFgaService>();
        if (registerAmbientProvider) {
            services.AddScoped(_ => stub!);
        }

        return services.BuildServiceProvider();
    }

    // Located by field type so the test is robust to the primary-constructor capture field's name.
    private static ISealedFgaAmbientOptionsProvider? AmbientProviderFieldOf(SealedFgaService service) {
        var field = typeof(SealedFgaService)
                   .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                   .Single(f => f.FieldType == typeof(ISealedFgaAmbientOptionsProvider));
        return (ISealedFgaAmbientOptionsProvider?) field.GetValue(service);
    }

    private sealed class StubAmbientProvider : ISealedFgaAmbientOptionsProvider {
        public ValueTask<SealedFgaQueryOptions?> GetCheckOptionsAsync<TObjId>(
            ISealedFgaUser user,
            ISealedFgaRelation<TObjId> relation,
            TObjId objectId,
            CancellationToken cancellationToken = default
        ) where TObjId : ISealedFgaTypeId<TObjId>
            => ValueTask.FromResult<SealedFgaQueryOptions?>(null);
    }

    #endregion

    #region Typed tuple operation projection

    [Fact]
    public void ToTupleKey_maps_typed_parts_to_raw_strings() {
        var user = TestUserId.Parse("alice");
        var obj = TestObjectId.New();

        var key = SealedFgaTupleOperation.Of(user, CanView, obj).ToTupleKey();

        key.User.ShouldBe("testuser:alice");
        key.Relation.ShouldBe("can_view");
        key.Object.ShouldBe(obj.AsOpenFgaIdTupleString());
    }

    [Fact]
    public void ToTupleKey_maps_userset_subjects_to_hash_notation() {
        var parent = TestParentId.New();
        var obj = TestObjectId.New();

        var key = SealedFgaTupleOperation
                 .Of(SealedFgaUserset<TestParentId>.From(parent, Member), CanView, obj)
                 .ToTupleKey();

        key.User.ShouldBe($"{parent.AsOpenFgaIdTupleString()}#Member");
    }

    [Fact]
    public void ToTupleKey_rejects_a_default_constructed_operation() =>
        Should.Throw<ArgumentException>(() => default(SealedFgaTupleOperation).ToTupleKey());

    [Fact]
    public void Of_rejects_null_parts() {
        var obj = TestObjectId.New();
        Should.Throw<ArgumentNullException>(() => SealedFgaTupleOperation.Of(null!, CanView, obj));
        Should.Throw<ArgumentNullException>(() => SealedFgaTupleOperation.Of(TestUserId.Parse("a"), null!, obj));
    }

    #endregion
}
