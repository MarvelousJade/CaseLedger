using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CaseLedger.Api.Contracts;

namespace CaseLedger.Api.Tests;

public sealed class JwtAuthenticationTests
{
    [Fact]
    public async Task SessionCanIssueBearerTokenForGatewayRequests()
    {
        using var factory = new CaseLedgerFactory();
        using var sessionClient = factory.CreateCookieClient();
        var analyst = await sessionClient.LoginAsync();

        var tokenResponse = await sessionClient.GetAsync("/api/auth/token");
        tokenResponse.EnsureSuccessStatusCode();
        Assert.Equal("no-store", tokenResponse.Headers.CacheControl?.ToString());
        var token = await tokenResponse.Content.ReadRequiredJsonAsync<AccessTokenResponse>();
        Assert.False(string.IsNullOrWhiteSpace(token.AccessToken));
        Assert.True(token.ExpiresAt > DateTime.UtcNow);

        using var gatewayClient = factory.CreateClient();
        gatewayClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var currentUser = await gatewayClient.GetFromJsonAsync<UserResponse>("/api/auth/me");
        Assert.Equal(analyst, currentUser);

        var cases = await gatewayClient.GetFromJsonAsync<CaseCollectionResponse>(
            "/api/cases?page=1&pageSize=1");
        var target = Assert.Single(Assert.IsType<CaseCollectionResponse>(cases).Items);
        using var update = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/cases/{target.Id:D}")
        {
            Content = JsonContent.Create(new { status = "InProgress" })
        };
        update.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{target.Version:D}\""));

        var updatedResponse = await gatewayClient.SendAsync(update);
        updatedResponse.EnsureSuccessStatusCode();
        var updated = await updatedResponse.Content.ReadRequiredJsonAsync<CaseDetailResponse>();
        Assert.Equal("InProgress", updated.Status);
    }

    [Fact]
    public async Task InvalidBearerTokenIsRejectedWithoutFallingBackToCookies()
    {
        using var factory = new CaseLedgerFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "not-a-valid-token");

        var response = await client.GetAsync("/api/cases");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
