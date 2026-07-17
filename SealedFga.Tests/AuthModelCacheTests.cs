using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using OpenFga.Sdk.Model;
using SealedFga.Fga;
using Shouldly;
using Xunit;

namespace SealedFga.Tests;

/// <summary>
///     Unit tests for <see cref="SealedFgaAuthModelCache" /> (internal API via InternalsVisibleTo):
///     pinned model IDs cache indefinitely, unpinned resolution is TTL-bounded, and any change of
///     pin state invalidates the snapshot.
/// </summary>
public class AuthModelCacheTests {
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private sealed class CountingFetcher {
        public int Fetches { get; private set; }

        public Task<AuthorizationModel> FetchAsync() {
            Fetches++;
            return Task.FromResult(new AuthorizationModel { Id = $"model-{Fetches}", TypeDefinitions = [] });
        }
    }

    [Fact]
    public async Task Pinned_model_is_cached_indefinitely() {
        var time = new FakeTimeProvider();
        var cache = new SealedFgaAuthModelCache(time);
        var fetcher = new CountingFetcher();

        var first = await cache.GetModelAsync("pinned-id", Ttl, fetcher.FetchAsync);
        time.Advance(TimeSpan.FromDays(365));
        var second = await cache.GetModelAsync("pinned-id", Ttl, fetcher.FetchAsync);

        fetcher.Fetches.ShouldBe(1);
        second.ShouldBeSameAs(first);
    }

    [Fact]
    public async Task Unpinned_model_refetches_only_after_ttl() {
        var time = new FakeTimeProvider();
        var cache = new SealedFgaAuthModelCache(time);
        var fetcher = new CountingFetcher();

        await cache.GetModelAsync(null, Ttl, fetcher.FetchAsync);
        time.Advance(Ttl - TimeSpan.FromSeconds(1));
        await cache.GetModelAsync(null, Ttl, fetcher.FetchAsync);
        fetcher.Fetches.ShouldBe(1); // still fresh

        time.Advance(TimeSpan.FromSeconds(2));
        var refetched = await cache.GetModelAsync(null, Ttl, fetcher.FetchAsync);
        fetcher.Fetches.ShouldBe(2); // stale -> refetched
        refetched.Id.ShouldBe("model-2");
    }

    [Fact]
    public async Task Pin_state_change_invalidates_the_snapshot() {
        var time = new FakeTimeProvider();
        var cache = new SealedFgaAuthModelCache(time);
        var fetcher = new CountingFetcher();

        await cache.GetModelAsync("pin-a", Ttl, fetcher.FetchAsync);
        await cache.GetModelAsync("pin-b", Ttl, fetcher.FetchAsync); // different pin -> refetch
        fetcher.Fetches.ShouldBe(2);

        await cache.GetModelAsync(null, Ttl, fetcher.FetchAsync); // unpinned now -> refetch
        fetcher.Fetches.ShouldBe(3);

        await cache.GetModelAsync("pin-b", Ttl, fetcher.FetchAsync); // re-pinned -> refetch
        fetcher.Fetches.ShouldBe(4);
    }
}
