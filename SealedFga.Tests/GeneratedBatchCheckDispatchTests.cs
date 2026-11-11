using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using OpenFga.Sdk.Model;
using SealedFga.AuthModel;
using SealedFga.Fga;
using SealedFga.Tests.Support;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

[Collection(GlobalCollection.Name)]
public sealed class GeneratedBatchCheckDispatchTests : IDisposable {
    private const string Model = """
        model
          schema 1.1
        type user
        type guid
          relations
            define can_view: [user]
            define Owner: [user]
        type number
          relations
            define Member: [user]
        type large
          relations
            define can_view: [user]
        type text
          relations
            define can_view: [user]
        """;

    private const string Source = """
        using SealedFga.Attributes;
        using SealedFga.Models;
        namespace TestApp;
        [SealedFgaTypeId("guid", SealedFgaTypeIdType.Guid)]
        public readonly partial record struct GuidId;
        [SealedFgaTypeId("number", SealedFgaTypeIdType.Int)]
        public readonly partial record struct NumberId;
        [SealedFgaTypeId("large", SealedFgaTypeIdType.Long)]
        public readonly partial record struct LargeId;
        [SealedFgaTypeId("text", SealedFgaTypeIdType.String)]
        public readonly partial record struct TextId;
        [SealedFgaTypeId("user", SealedFgaTypeIdType.String)]
        public readonly partial record struct UserId;
        """;

    private readonly AssemblyLoadContext _context = new(nameof(GeneratedBatchCheckDispatchTests), isCollectible: true);
    private readonly ISealedFgaUser _user = new TestUserId("batch-user");
    private readonly Func<ISealedFgaService, ISealedFgaUser, IReadOnlyList<TypeNameCheck>, SealedFgaQueryOptions?, CancellationToken,
        Task<IReadOnlyList<bool>?>> _batch;
    private readonly Func<ISealedFgaService, ISealedFgaUser, string, string, string, SealedFgaQueryOptions?, CancellationToken,
        Task<bool?>> _single;

    public GeneratedBatchCheckDispatchTests() {
        var generated = GeneratorTestHarness.Run(Source, Model).GeneratedTrees.Where(t => {
            var name = Path.GetFileName(t.FilePath);
            return name == "SealedFgaCheckDispatch.g.cs" || !name.StartsWith("SealedFga");
        });
        var compilation = GeneratorTestHarness.CreateCompilation(Source).AddSyntaxTrees(generated);
        using var image = new MemoryStream();
        var emit = compilation.Emit(image);
        emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        image.Position = 0;
        var dispatch = _context.LoadFromStream(image).GetType("SealedFga.SealedFgaCheckDispatch", true)!;
        _batch = dispatch.GetMethod("TryBatchCheckByTypeNameAsync")!.CreateDelegate<
            Func<ISealedFgaService, ISealedFgaUser, IReadOnlyList<TypeNameCheck>, SealedFgaQueryOptions?, CancellationToken,
                Task<IReadOnlyList<bool>?>>>();
        _single = dispatch.GetMethod("TryCheckByTypeNameAsync")!.CreateDelegate<
            Func<ISealedFgaService, ISealedFgaUser, string, string, string, SealedFgaQueryOptions?, CancellationToken,
                Task<bool?>>>();
    }

    public void Dispose() => _context.Unload();

    [Fact]
    public async Task Mixed_types_preserve_order_duplicates_and_single_check_parity() {
        TypeNameCheck[] checks = [
            new("number", "Member", "42"),
            new("text", "can_view", "denied"),
            new("guid", "Owner", "{12345678-1234-1234-1234-123456789012}"),
            new("number", "unknown_relation", "+0042"),
            new("large", "can_view", "9223372036854775807"),
            new("text", "can_view", "allowed"),
            new("number", "Member", "42"),
            new("text", "CAN_VIEW", ""),
        ];
        var svc = new FakeService();
        var fake = svc;
        using var cts = new CancellationTokenSource();
        var options = new SealedFgaQueryOptions {
            ContextualTuples = [SealedFgaContextualTuple.Of(_user, new TestObjectRelation("can_view"), TestObjectId.New())],
        };
        var actual = await _batch(svc, _user, checks, options, cts.Token);
        actual.ShouldBe([true, false, true, true, true, true, true, true]);
        fake.BatchTypes.Order().ShouldBe(["GuidId", "LargeId", "NumberId", "TextId"]);
        fake.Calls.Count.ShouldBe(checks.Length);
        var batchCalls = fake.Calls.ToArray();
        fake.Calls.Clear();
        var expected = new List<bool>();
        foreach (var check in checks) {
            expected.Add((await _single(svc, _user, check.TypeName, check.Relation, check.ObjectId, options, cts.Token))!.Value);
        }
        actual.ShouldBe(expected);
        // Compare typed IDs and exact relation strings, not just the fake's answers.
        foreach (var call in fake.Calls)
            batchCalls.ShouldContain(call);
        fake.Options.ShouldAllBe(o => ReferenceEquals(o, options));
        fake.Tokens.ShouldAllBe(t => t == cts.Token);
        fake.Users.ShouldAllBe(u => ReferenceEquals(u, _user));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("user")] // known to the model but has no checkable relations
    [InlineData("NUMBER")]
    public async Task Any_unknown_type_returns_null_without_calls(string type) {
        var svc = new FakeService();
        (await _batch(svc, _user, [new("number", "Member", "bad-id"), new(type, "x", "x")], null, default))
            .ShouldBeNull();
        (await _single(svc, _user, type, "x", "x", null, default)).ShouldBeNull();
        svc.Calls.ShouldBeEmpty();
        svc.BatchTypes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Empty_input_makes_no_calls() {
        var svc = new FakeService();
        (await _batch(svc, _user, [], null, default))!.ShouldBeEmpty();
        svc.BatchTypes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Default_options_are_forwarded_to_the_typed_batch() {
        var svc = new FakeService();
        (await _batch(svc, _user, [new("number", "Member", "42")], null, default)).ShouldBe([true]);
        svc.Options.ShouldBe([null]);
        svc.Tokens.ShouldBe([CancellationToken.None]);
        svc.BatchTypes.ShouldBe(["NumberId"]);
    }

    [Theory]
    [InlineData("guid", "invalid", typeof(FormatException))]
    [InlineData("number", "invalid", typeof(FormatException))]
    [InlineData("number", "2147483648", typeof(OverflowException))]
    [InlineData("large", "9223372036854775808", typeof(OverflowException))]
    public async Task Invalid_ids_throw_the_same_exception_as_single_checks(string type, string id, Type exceptionType) {
        var svc = new FakeService();
        var batchError = await Record.ExceptionAsync(() => _batch(svc, _user, [new(type, "anything", id)], null, default));
        var singleError = await Record.ExceptionAsync(() => _single(svc, _user, type, "anything", id, null, default));
        batchError!.GetType().ShouldBe(exceptionType);
        singleError!.GetType().ShouldBe(exceptionType);
        svc.Calls.ShouldBeEmpty();
    }

    private sealed class FakeService : ISealedFgaService {
        public List<string> BatchTypes { get; } = [];
        public List<(Type Type, string Relation, object Id)> Calls { get; } = [];
        public List<SealedFgaQueryOptions?> Options { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public List<ISealedFgaUser> Users { get; } = [];

        public Task<bool> CheckAsync<T>(
            ISealedFgaUser user, ISealedFgaRelation<T> relation, T objectId,
            SealedFgaQueryOptions? queryOptions = null, CancellationToken cancellationToken = default
        ) where T : ISealedFgaTypeId<T> {
            Options.Add(queryOptions);
            Tokens.Add(cancellationToken);
            return Task.FromResult(Allowed(user, relation, objectId));
        }

        public Task<Dictionary<(ISealedFgaUser User, ISealedFgaRelation<T> Relation, T Object), bool>> BatchCheckAsync<T>(
            IEnumerable<(ISealedFgaUser User, ISealedFgaRelation<T> Relation, T Object)> checks,
            SealedFgaQueryOptions? queryOptions = null, CancellationToken cancellationToken = default
        ) where T : ISealedFgaTypeId<T> {
            Options.Add(queryOptions);
            Tokens.Add(cancellationToken);
            BatchTypes.Add(typeof(T).Name);
            var result = new Dictionary<(ISealedFgaUser, ISealedFgaRelation<T>, T), bool>();
            foreach (var check in checks) {
                result[check] = Allowed(check.User, check.Relation, check.Object);
            }
            return Task.FromResult(result);
        }

        private bool Allowed<T>(ISealedFgaUser user, ISealedFgaRelation<T> relation, T id) where T : ISealedFgaTypeId<T> {
            Users.Add(user);
            Calls.Add((typeof(T), relation.AsOpenFgaString(), id));
            return id.ToString() != "denied";
        }

        public Task DeleteObjectFromOpenFgaIncludingAllRelations<TObjId>(TObjId objId)
            where TObjId : ISealedFgaTypeId<TObjId> => throw new NotSupportedException();

        public Task DeleteAllRelationsForRawObjectAsync(
            string rawObjectId,
            string typeName,
            CancellationToken cancellationToken = new()
        ) => throw new NotSupportedException();

        public Task EnsureCheckAsync<TObjId>(
            ISealedFgaUser user,
            ISealedFgaRelation<TObjId> relation,
            TObjId objectId,
            SealedFgaQueryOptions? queryOptions = null,
            CancellationToken cancellationToken = new()
        )
            where TObjId : ISealedFgaTypeId<TObjId> => throw new NotSupportedException();

        public Task<IEnumerable<TObjId>> ListObjectsAsync<TObjId>(
            ISealedFgaUser user,
            ISealedFgaRelation<TObjId> relation,
            SealedFgaQueryOptions? queryOptions = null,
            CancellationToken cancellationToken = new()
        )
            where TObjId : ISealedFgaTypeId<TObjId> => throw new NotSupportedException();

        public Task<SealedFgaListUsersResult<TUserId>> ListUsersAsync<TObjId, TUserId>(
            TObjId objectId,
            ISealedFgaRelation<TObjId> relation,
            SealedFgaQueryOptions? queryOptions = null,
            CancellationToken cancellationToken = new()
        )
            where TObjId : ISealedFgaTypeId<TObjId>
            where TUserId : ISealedFgaTypeId<TUserId> => throw new NotSupportedException();

        public Task<IReadOnlyList<ISealedFgaRelation<TObjId>>> ListRelationsAsync<TObjId>(
            ISealedFgaUser user,
            IEnumerable<ISealedFgaRelation<TObjId>> relations,
            TObjId objectId,
            SealedFgaQueryOptions? queryOptions = null,
            CancellationToken cancellationToken = new()
        )
            where TObjId : ISealedFgaTypeId<TObjId> => throw new NotSupportedException();

        public Task<Dictionary<(ISealedFgaUser User, ISealedFgaRelation<TObjId> Relation, TObjId Object), bool>>
            BatchCheckAsync<TObjId>(
                IEnumerable<(ISealedFgaUser User, ISealedFgaRelation<TObjId> Relation, TObjId Object)> checks,
                Func<(ISealedFgaUser User, ISealedFgaRelation<TObjId> Relation, TObjId Object),
                    IReadOnlyCollection<SealedFgaContextualTuple>?> contextualTuplesFactory,
                ConsistencyPreference? consistency = null,
                CancellationToken cancellationToken = new()
            )
            where TObjId : ISealedFgaTypeId<TObjId> => throw new NotSupportedException();

        public Task WriteAsync<TObjId>(
            ISealedFgaUser user,
            ISealedFgaRelation<TObjId> relation,
            TObjId objectId,
            CancellationToken ct = new()
        )
            where TObjId : ISealedFgaTypeId<TObjId> => throw new NotSupportedException();

        public Task DeleteAsync<TObjId>(
            ISealedFgaUser user,
            ISealedFgaRelation<TObjId> relation,
            TObjId objectId,
            CancellationToken ct = new()
        )
            where TObjId : ISealedFgaTypeId<TObjId> => throw new NotSupportedException();

        public Task WriteAsync(IReadOnlyCollection<SealedFgaTupleOperation> writes, CancellationToken ct = new()) => throw new NotSupportedException();

        public Task DeleteAsync(IReadOnlyCollection<SealedFgaTupleOperation> deletes, CancellationToken ct = new()) => throw new NotSupportedException();

        public Task ApplyAsync(
            IReadOnlyCollection<SealedFgaTupleOperation> writes,
            IReadOnlyCollection<SealedFgaTupleOperation> deletes,
            CancellationToken ct = new()
        ) => throw new NotSupportedException();
    }
}
