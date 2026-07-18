using System.Net.Http.Json;
using CaseLedger.Api.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CaseLedger.Api.Tests;

internal static class ApiTestHelpers
{
    public const string AntiforgeryHeaderName = "X-CSRF-TOKEN";

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
        await client.RefreshAntiforgeryTokenAsync();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        var user = await response.Content.ReadRequiredJsonAsync<UserResponse>();
        await client.RefreshAntiforgeryTokenAsync();
        return user;
    }

    public static async Task<string> RefreshAntiforgeryTokenAsync(this HttpClient client)
    {
        var response = await client.GetAsync("/api/auth/antiforgery");
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadRequiredJsonAsync<AntiforgeryTokenResponse>()).Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("The antiforgery endpoint returned an empty token.");
        }

        client.DefaultRequestHeaders.Remove(AntiforgeryHeaderName);
        client.DefaultRequestHeaders.Add(AntiforgeryHeaderName, token);
        return token;
    }

    public static async Task<T> ReadRequiredJsonAsync<T>(this HttpContent content)
    {
        return await content.ReadFromJsonAsync<T>() ??
               throw new InvalidOperationException($"The response did not contain {typeof(T).Name} JSON.");
    }
}
