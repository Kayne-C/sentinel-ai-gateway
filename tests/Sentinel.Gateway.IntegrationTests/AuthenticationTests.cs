using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Sentinel.Domain.Identity;

namespace Sentinel.Gateway.IntegrationTests;

public sealed class AuthenticationTests(KnowledgeGatewayFactory factory) : IClassFixture<KnowledgeGatewayFactory>
{
    private static readonly object Question = new { question = "izin politikası" };

    [Theory]
    [InlineData("/api/v1/usage")]
    [InlineData("/api/v1/documents")]
    [InlineData("/api/v1/audit")]
    public async Task Endpoints_need_a_token(string url)
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Asking_needs_a_token()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/ask", Question)).StatusCode);
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_rejected()
    {
        var forged = GatewayFactory.CustomToken(ValidClaims(), signingKey: "an-attacker-chosen-signing-key-0123456789");
        using var client = Bearer(forged);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/ask", Question)).StatusCode);
    }

    [Fact]
    public async Task An_expired_token_is_rejected()
    {
        var expired = GatewayFactory.CustomToken(ValidClaims(), expires: DateTime.UtcNow.AddMinutes(-10));
        using var client = Bearer(expired);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/ask", Question)).StatusCode);
    }

    [Fact]
    public async Task A_tenant_the_gateway_does_not_serve_is_refused_even_with_a_valid_token()
    {
        var token = GatewayFactory.CustomToken(ValidClaims(tenant: "11111111-2222-3333-4444-555555555555"));
        using var client = Bearer(token);

        var response = await client.PostAsJsonAsync("/api/v1/ask", Question);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Auth.TenantNotAllowed", (string?)(await response.ReadJsonAsync())["code"]);
    }

    [Fact]
    public async Task A_token_without_the_complete_group_list_is_refused_not_guessed()
    {
        // Entra drops "groups" above ~200 memberships and points to Graph instead.
        var overage = ValidClaims().Append(new Claim("_claim_names", "{\"groups\":\"src1\"}"));
        using var client = Bearer(GatewayFactory.CustomToken(overage));

        var response = await client.PostAsJsonAsync("/api/v1/ask", Question);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Auth.GroupsOverage", (string?)(await response.ReadJsonAsync())["code"]);
    }

    [Fact]
    public async Task A_token_without_tenant_and_subject_cannot_be_used()
    {
        using var client = Bearer(GatewayFactory.CustomToken([new Claim("roles", SentinelRoles.User)]));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/ask", Question)).StatusCode);
    }

    [Fact]
    public async Task Roles_decide_which_surface_a_caller_may_use()
    {
        using var user = factory.ClientFor("ayse");
        using var admin = factory.ClientFor("admin");
        using var app = factory.ClientFor("reporting-app");

        // A plain user asks questions but neither manages documents nor reads the audit log nor uses the proxy.
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/usage")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/v1/models")).StatusCode);

        // A workload with only the proxy role cannot ask questions over the knowledge base.
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("/v1/models")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("/api/v1/usage")).StatusCode); // a workload may read its own budget
        Assert.Equal(HttpStatusCode.Forbidden, (await app.PostAsJsonAsync("/api/v1/ask", Question)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetAsync("/api/v1/audit")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/audit")).StatusCode);
    }

    [Fact]
    public async Task Errors_are_problem_details_with_a_machine_readable_code_and_no_internals()
    {
        using var client = factory.ClientFor("deniz");
        var response = await client.GetAsync("/api/v1/audit");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Responses_are_never_cacheable_and_carry_hardening_headers()
    {
        using var client = factory.ClientFor("ayse");
        var response = await client.GetAsync("/api/v1/usage");

        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(response.Headers.Contains("Server"));
    }

    private HttpClient Bearer(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static IEnumerable<Claim> ValidClaims(string tenant = Tenants.Contoso) =>
    [
        new("tid", tenant),
        new("oid", "aaaaaaaa-0000-4000-8000-000000000001"),
        new("name", "Test User"),
        new("roles", SentinelRoles.User),
    ];
}
