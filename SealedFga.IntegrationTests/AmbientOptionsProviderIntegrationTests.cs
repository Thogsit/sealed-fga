using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using SealedFga;
using SealedFga.AuthModel;
using SealedFga.Fga;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.IntegrationTests;

/// <summary>
///     The direct-service analogue of <see cref="HeaderContextualTupleProvider" />: proves that an
///     <see cref="ISealedFgaAmbientOptionsProvider" /> registered on <see cref="SealedFgaService" /> is
///     consulted per concrete object on <c>Check</c>/<c>BatchCheck</c>/<c>ListRelations</c>, and that
///     its options merge with explicit per-call options — exercising the real OpenFGA server, not a
///     fake. Uses the union <c>can_edit</c> relation ([testuser] or <c>Member</c> from <c>OwnedBy</c>):
///     the ambient provider supplies the <c>Member</c> leg contextually, so a computed grant fires only
///     when the provider injects it.
/// </summary>
[Collection(OpenFgaCollection.Name)]
[Trait("Category", "Integration")]
public class AmbientOptionsProviderIntegrationTests(OpenFgaFixture fga) {
    /// <summary>
    ///     Injects a contextual <c>Member</c> grant (activating the computed <c>can_edit</c> arm) only
    ///     for one target object — the "super-user for this object" shape. Every other object gets
    ///     <c>null</c>, i.e. unchanged behavior. Also records how many times it was consulted so the
    ///     per-item batch resolution can be asserted.
    /// </summary>
    private sealed class MemberGrantAmbientProvider(TestObjectId target, TestParentId parent)
        : ISealedFgaAmbientOptionsProvider {
        public int Calls { get; private set; }

        public ValueTask<SealedFgaQueryOptions?> GetCheckOptionsAsync<TObjId>(
            ISealedFgaUser user,
            ISealedFgaRelation<TObjId> relation,
            TObjId objectId,
            CancellationToken cancellationToken = default
        ) where TObjId : ISealedFgaTypeId<TObjId> {
            Calls++;
            if (objectId.AsOpenFgaIdTupleString() != target.AsOpenFgaIdTupleString()) {
                return ValueTask.FromResult<SealedFgaQueryOptions?>(null);
            }

            return ValueTask.FromResult<SealedFgaQueryOptions?>(new SealedFgaQueryOptions {
                ContextualTuples = [SealedFgaContextualTuple.Of(user, new ParentRelation("Member"), parent)],
            });
        }
    }

    private SealedFgaService ServiceWith(ISealedFgaAmbientOptionsProvider provider)
        => new(fga.Client, Options.Create(new SealedFgaOptions()), null, provider);

    [Fact]
    public async Task Ambient_tuple_grants_a_single_check_without_a_per_call_option() {
        var parent = TestParentId.New();
        var obj = TestObjectId.New();
        var user = new TestUserId("ambient-single");
        await fga.Service.WriteAsync([SealedFgaTupleOperation.Of(parent, new ObjRelation("OwnedBy"), obj)]);

        var service = ServiceWith(new MemberGrantAmbientProvider(obj, parent));

        // No stored membership, no per-call options — the ambient Member tuple activates can_edit.
        (await service.CheckAsync(user, new ObjRelation("can_edit"), obj)).ShouldBeTrue();
        // Nothing was stored: the plain fixture service (no provider) still sees no access.
        (await fga.Service.CheckAsync(user, new ObjRelation("can_edit"), obj)).ShouldBeFalse();
    }

    [Fact]
    public async Task Ambient_and_per_call_contextual_tuples_are_unioned() {
        var parent = TestParentId.New();
        var obj = TestObjectId.New();
        var user = new TestUserId("ambient-union");
        await fga.Service.WriteAsync([SealedFgaTupleOperation.Of(parent, new ObjRelation("OwnedBy"), obj)]);

        var service = ServiceWith(new MemberGrantAmbientProvider(obj, parent));

        // Per-call options carry an unrelated can_view contextual tuple; ambient carries the Member leg.
        // Both must be sent: can_edit (needs the ambient Member) AND can_view (needs the per-call tuple).
        var perCall = new SealedFgaQueryOptions {
            ContextualTuples = [SealedFgaContextualTuple.Of(user, new ObjRelation("can_view"), obj)],
        };

        (await service.CheckAsync(user, new ObjRelation("can_edit"), obj, perCall)).ShouldBeTrue();
        (await service.CheckAsync(user, new ObjRelation("can_view"), obj, perCall)).ShouldBeTrue();
    }

    [Fact]
    public async Task Ambient_provider_is_resolved_per_item_in_a_batch() {
        var parent = TestParentId.New();
        var target = TestObjectId.New();
        var other = TestObjectId.New();
        var user = new TestUserId("ambient-batch");
        var canEdit = new ObjRelation("can_edit");
        await fga.Service.WriteAsync([
            SealedFgaTupleOperation.Of(parent, new ObjRelation("OwnedBy"), target),
            SealedFgaTupleOperation.Of(parent, new ObjRelation("OwnedBy"), other),
        ]);

        var provider = new MemberGrantAmbientProvider(target, parent);
        var service = ServiceWith(provider);

        var results = await service.BatchCheckAsync([
            ((ISealedFgaUser) user, (ISealedFgaRelation<TestObjectId>) canEdit, target),
            (user, canEdit, other),
        ]);

        results[(user, canEdit, target)].ShouldBeTrue();  // ambient injected for this object
        results[(user, canEdit, other)].ShouldBeFalse();  // provider returned null for this object
        provider.Calls.ShouldBe(2);                       // resolved once per item
    }

    [Fact]
    public async Task Ambient_tuple_is_applied_to_list_relations() {
        var parent = TestParentId.New();
        var obj = TestObjectId.New();
        var user = new TestUserId("ambient-listrel");
        await fga.Service.WriteAsync([SealedFgaTupleOperation.Of(parent, new ObjRelation("OwnedBy"), obj)]);

        var service = ServiceWith(new MemberGrantAmbientProvider(obj, parent));

        ISealedFgaRelation<TestObjectId>[] candidates = [new ObjRelation("can_view"), new ObjRelation("can_edit")];
        var held = await service.ListRelationsAsync(user, candidates, obj);

        // Only can_edit is granted (via the ambient Member tuple activating the computed arm).
        held.Select(r => r.AsOpenFgaString()).ShouldBe(["can_edit"]);
    }

    [Fact]
    public async Task No_provider_leaves_behavior_unchanged() {
        var parent = TestParentId.New();
        var obj = TestObjectId.New();
        var user = new TestUserId("ambient-none");
        await fga.Service.WriteAsync([SealedFgaTupleOperation.Of(parent, new ObjRelation("OwnedBy"), obj)]);

        // The fixture service has no ambient provider: can_edit stays false with no per-call options.
        (await fga.Service.CheckAsync(user, new ObjRelation("can_edit"), obj)).ShouldBeFalse();
    }
}
