using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using SealedFga.Sample.Auth;
using Shouldly;
using Xunit;

namespace SealedFga.IntegrationTests;

/// <summary>
///     End-to-end HTTP tests for the FGA model binders on the Sample <c>SecretController</c>, driven through
///     <see cref="SampleWebAppFixture" /> (real host + real OpenFGA). The mock auth handler authenticates
///     every request as <c>user:some-id</c>, a Member of Agency One, which can view secrets 1 &amp; 2 (both
///     owned by Agency One) but not secret 3 (owned by Agency Two).
/// </summary>
[Collection(SampleWebAppCollection.Name)]
[Trait("Category", "Integration")]
public class SecretEndpointsIntegrationTests(SampleWebAppFixture fixture) {
    private const string Agency1Id = "b490657a-2c5f-4bab-b540-3753d02aca53";
    private const string Secret1Id = "f6c603cd-881c-4433-ae74-8a0b4e70d67b"; // Agency One
    private const string Secret2Id = "c4ffff35-0c6d-4d1a-847c-1dfda5fa9bd9"; // Agency One
    private const string Secret3Id = "0240d59b-2d04-4391-9700-1902b75385b9"; // Agency Two

    [Fact]
    public async Task GetSecretById_authorized_includes_navigation() {
        var client = fixture.CreateClient();

        var response = await client.GetAsync($"/secrets/{Secret1Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.GetProperty("id").GetString().ShouldBe(Secret1Id);
        root.GetProperty("owningAgencyId").GetString().ShouldBe(Agency1Id);

        // The [FgaAuthorize(..., Include = [nameof(SecretEntityIncludes.OwningAgency)])] eager-loads the nav.
        var owningAgency = root.GetProperty("owningAgency");
        owningAgency.ValueKind.ShouldBe(JsonValueKind.Object);
        owningAgency.GetProperty("id").GetString().ShouldBe(Agency1Id);
        owningAgency.GetProperty("name").GetString().ShouldBe("Agency One");
    }

    [Fact]
    public async Task GetAllSecrets_returns_only_authorized_without_navigation() {
        var client = fixture.CreateClient();

        var response = await client.GetAsync("/secrets");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var secrets = doc.RootElement.EnumerateArray().ToList();

        var ids = secrets.Select(s => s.GetProperty("id").GetString()).ToList();
        ids.ShouldContain(Secret1Id);
        ids.ShouldContain(Secret2Id);
        ids.ShouldNotContain(Secret3Id); // owned by Agency Two — user is not authorized

        // The list endpoint declares no Include, so the navigation must stay null.
        foreach (var secret in secrets) {
            secret.GetProperty("owningAgency").ValueKind.ShouldBe(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task GetSecretsPaged_pages_the_authorized_query_in_the_database() {
        var client = fixture.CreateClient();

        // The user can view exactly secrets 1 & 2; the endpoint orders by Id, so the two pages of
        // size 1 partition them — and secret 3 must never appear on any page.
        var page0 = await GetIds(client, "/secrets/paged?page=0&pageSize=1");
        var page1 = await GetIds(client, "/secrets/paged?page=1&pageSize=1");
        var page2 = await GetIds(client, "/secrets/paged?page=2&pageSize=1");

        page0.Count.ShouldBe(1);
        page1.Count.ShouldBe(1);
        page2.ShouldBeEmpty();
        page0.Concat(page1).ShouldBe([Secret1Id, Secret2Id], ignoreOrder: true);
    }

    [Fact]
    public async Task GetAllSecrets_with_contextual_tuple_includes_otherwise_hidden_secret() {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderContextualTupleProvider.HeaderName, Secret3Id);

        var ids = await GetIds(client, "/secrets");

        ids.ShouldContain(Secret3Id); // granted for this call only, via the binder options hook

        // Without the header the grant is gone — nothing was stored.
        var idsWithout = await GetIds(fixture.CreateClient(), "/secrets");
        idsWithout.ShouldNotContain(Secret3Id);
    }

    [Fact]
    public async Task GetAllSecrets_with_full_access_verdict_returns_every_secret() {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderContextualTupleProvider.FullAccessHeaderName, "true");

        var ids = await GetIds(client, "/secrets");

        // FullAccess skips ListObjects and hands over the whole DbSet, so even Agency Two's secret
        // (which the user cannot normally view) is returned.
        ids.ShouldContain(Secret1Id);
        ids.ShouldContain(Secret2Id);
        ids.ShouldContain(Secret3Id);
    }

    [Fact]
    public async Task GetAllSecrets_with_scoped_verdict_returns_only_the_scoped_ids() {
        var client = fixture.CreateClient();
        // Scope to secret 3 only — an ID the user is NOT normally authorized for. This proves the
        // verdict bypasses ListObjects entirely (rather than intersecting with it).
        client.DefaultRequestHeaders.Add(HeaderContextualTupleProvider.ScopeIdsHeaderName, Secret3Id);

        var ids = await GetIds(client, "/secrets");

        ids.ShouldBe([Secret3Id]);
    }

    [Fact]
    public async Task GetSecretById_with_contextual_tuple_allows_otherwise_forbidden_secret() {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderContextualTupleProvider.HeaderName, Secret3Id);

        var response = await client.GetAsync($"/secrets/{Secret3Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<System.Collections.Generic.List<string?>> GetIds(
        System.Net.Http.HttpClient client,
        string url
    ) {
        var response = await client.GetAsync(url);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToList();
    }

    [Fact]
    public async Task GetSecretById_unauthorized_returns_403() {
        var client = fixture.CreateClient();

        var response = await client.GetAsync($"/secrets/{Secret3Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetSecretById_without_user_claim_returns_401() {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add(MockAuthenticationHandler.OmitUserClaimHeader, "true");

        var response = await client.GetAsync($"/secrets/{Secret1Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetAllSecrets_without_user_claim_returns_401() {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add(MockAuthenticationHandler.OmitUserClaimHeader, "true");

        var response = await client.GetAsync("/secrets");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
