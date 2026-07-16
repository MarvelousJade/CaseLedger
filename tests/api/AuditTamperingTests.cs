using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaseLedger.Api.Contracts;
using CaseLedger.Api.Data;
using CaseLedger.Api.Domain;
using CaseLedger.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseLedger.Api.Tests;

public sealed class AuditTamperingTests
{
    [Fact]
    public async Task VerificationDetectsCanonicalDataTampering()
    {
        using var factory = new CaseLedgerFactory();
        using var client = factory.CreateCookieClient();
        await client.LoginAsync("admin@caseledger.dev", "Admin123!");

        var cases = await client.GetFromJsonAsync<CaseCollectionResponse>("/api/cases?limit=100");
        Assert.NotNull(cases);
        var target = Assert.Single(cases.Items, item => item.Reference == "CL-2026-001");

        var before = await client.GetFromJsonAsync<AuditVerificationResponse>(
            $"/api/cases/{target.Id:D}/audit/verify");
        Assert.NotNull(before);
        Assert.True(before.Valid);
        Assert.Equal(2, before.CheckedEvents);

        await using (var immutableScope = factory.Services.CreateAsyncScope())
        {
            var db = immutableScope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
            var auditEvent = await db.AuditEvents.FirstAsync(item => item.CaseId == target.Id);
            auditEvent.Description = "Attempted tracked edit";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await using (var tamperScope = factory.Services.CreateAsyncScope())
        {
            var db = tamperScope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
            const string tampered = "{\"tampered\":true}";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AuditEvents\" SET \"CanonicalData\" = {tampered} WHERE \"CaseId\" = {target.Id} AND \"Sequence\" = 1");
        }

        var after = await client.GetFromJsonAsync<AuditVerificationResponse>(
            $"/api/cases/{target.Id:D}/audit/verify");
        Assert.NotNull(after);
        Assert.False(after.Valid);
        Assert.Equal(1, after.CheckedEvents);
        Assert.Equal(1, after.BrokenAt);

        var graphQlResponse = await client.PostAsJsonAsync("/graphql", new
        {
            query = "{ dashboard { integrityStatus } }"
        });
        graphQlResponse.EnsureSuccessStatusCode();
        using var graphQl = JsonDocument.Parse(await graphQlResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            "compromised",
            graphQl.RootElement
                .GetProperty("data")
                .GetProperty("dashboard")
                .GetProperty("integrityStatus")
                .GetString());
    }

    [Fact]
    public void HashFormulaUsesLowercaseSha256OfPreviousHashNewlineCanonicalData()
    {
        const string previousHash =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string canonicalData = "{\"eventType\":\"CaseCreated\",\"sequence\":1}";
        var expected = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{previousHash}\n{canonicalData}")))
            .ToLowerInvariant();

        var actual = AuditChainService.ComputeHash(previousHash, canonicalData);

        Assert.Equal(expected, actual);
        Assert.Matches("^[0-9a-f]{64}$", actual);
    }
}
