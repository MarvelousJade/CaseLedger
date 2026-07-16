using System.Net;
using System.Net.Http.Json;
using CaseLedger.Api.Contracts;

namespace CaseLedger.Api.Tests;

public sealed class RateLimitTests
{
    [Fact]
    public async Task LoginRejectsRequestsBeyondThePerMinuteLimit()
    {
        using var factory = new CaseLedgerFactory();
        using var client = factory.CreateClient();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/auth/login",
                new LoginRequest("analyst@caseledger.dev", "incorrect"));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var limited = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("analyst@caseledger.dev", "incorrect"));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }
}
