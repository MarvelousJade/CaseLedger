using System.Net.Http.Json;
using CaseLedger.Api.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CaseLedger.Api.Tests;

internal static class ApiTestHelpers
{
    public static HttpClient CreateCookieClient(this CaseLedgerFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    public static async Task<UserResponse> LoginAsync(
        this HttpClient client,
        string email = "analyst@caseledger.dev",
        string password = "Analyst123!")
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadRequiredJsonAsync<UserResponse>();
    }

    public static async Task<T> ReadRequiredJsonAsync<T>(this HttpContent content)
    {
        return await content.ReadFromJsonAsync<T>() ??
               throw new InvalidOperationException($"The response did not contain {typeof(T).Name} JSON.");
    }
}
