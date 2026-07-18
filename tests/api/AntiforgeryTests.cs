using System.Net;
using System.Net.Http.Json;
using CaseLedger.Api.Contracts;

namespace CaseLedger.Api.Tests;

public sealed class AntiforgeryTests
{
    [Fact]
    public async Task TokenEndpointIssuesNoStoreTokenAndProtectedCookie()
    {
        using var factory = new CaseLedgerFactory();
        using var client = factory.CreateCookieClient();

        var response = await client.GetAsync("/api/auth/antiforgery");

        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        var payload = await response.Content.ReadRequiredJsonAsync<AntiforgeryTokenResponse>();
        Assert.False(string.IsNullOrWhiteSpace(payload.Token));
        var cookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("CaseLedger.Antiforgery=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoginRequiresMatchingCookieAndRequestTokens()
    {
        using var factory = new CaseLedgerFactory();
        using var client = factory.CreateCookieClient();
        var credentials = new LoginRequest("analyst@caseledger.dev", "Analyst123!");

        var missing = await client.PostAsJsonAsync("/api/auth/login", credentials);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        await client.RefreshAntiforgeryTokenAsync();
        client.DefaultRequestHeaders.Remove(ApiTestHelpers.AntiforgeryHeaderName);
        client.DefaultRequestHeaders.Add(ApiTestHelpers.AntiforgeryHeaderName, "invalid-token");
        var invalid = await client.PostAsJsonAsync("/api/auth/login", credentials);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        await client.RefreshAntiforgeryTokenAsync();
        var accepted = await client.PostAsJsonAsync("/api/auth/login", credentials);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedMutationRequiresTokenBoundToCurrentIdentity()
    {
        using var factory = new CaseLedgerFactory();
        using var client = factory.CreateCookieClient();
        var anonymousToken = await client.RefreshAntiforgeryTokenAsync();
        var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("analyst@caseledger.dev", "Analyst123!"));
        login.EnsureSuccessStatusCode();
        var analyst = await login.Content.ReadRequiredJsonAsync<UserResponse>();
        var request = ValidCase(analyst.Id);

        var identityMismatch = await client.PostAsJsonAsync("/api/cases", request);
        Assert.Equal(HttpStatusCode.BadRequest, identityMismatch.StatusCode);
        Assert.Equal(
            anonymousToken,
            Assert.Single(client.DefaultRequestHeaders.GetValues(
                ApiTestHelpers.AntiforgeryHeaderName)));

        client.DefaultRequestHeaders.Remove(ApiTestHelpers.AntiforgeryHeaderName);
        var missing = await client.PostAsJsonAsync("/api/cases", request);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var cases = await client.GetFromJsonAsync<CaseCollectionResponse>("/api/cases?limit=100");
        Assert.NotNull(cases);
        Assert.Equal(5, cases.Total);

        await client.RefreshAntiforgeryTokenAsync();
        var accepted = await client.PostAsJsonAsync("/api/cases", request);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    [Fact]
    public async Task GraphQlAndLogoutPostsRequireAntiforgeryToken()
    {
        using var factory = new CaseLedgerFactory();
        using var client = factory.CreateCookieClient();
        await client.LoginAsync();
        client.DefaultRequestHeaders.Remove(ApiTestHelpers.AntiforgeryHeaderName);

        var graphQl = await client.PostAsJsonAsync("/graphql", new
        {
            query = "{ dashboard { totalCases } }"
        });
        Assert.Equal(HttpStatusCode.BadRequest, graphQl.StatusCode);

        var rejectedLogout = await client.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, rejectedLogout.StatusCode);
        var stillAuthenticated = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, stillAuthenticated.StatusCode);

        await client.RefreshAntiforgeryTokenAsync();
        var acceptedGraphQl = await client.PostAsJsonAsync("/graphql", new
        {
            query = "{ dashboard { totalCases } }"
        });
        acceptedGraphQl.EnsureSuccessStatusCode();

        var acceptedLogout = await client.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, acceptedLogout.StatusCode);
        var signedOut = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
    }

    private static CreateCaseRequest ValidCase(Guid assigneeId) =>
        new(
            "Review antiforgery regression",
            "Confirm that browser requests cannot mutate case records without a matching request token.",
            "Medium",
            "Application Security",
            assigneeId,
            DateTime.UtcNow.AddDays(7),
            ["security"]);
}
