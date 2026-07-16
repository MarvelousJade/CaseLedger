using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaseLedger.Api.Contracts;
using CaseLedger.Api.Services;

namespace CaseLedger.Api.Tests;

public sealed class ApiLifecycleTests
{
    [Fact]
    public async Task AuthenticatedAnalystCanCompleteCaseLifecycle()
    {
        using var factory = new CaseLedgerFactory();
        using var client = factory.CreateCookieClient();

        var unauthorized = await client.GetAsync("/api/cases");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal("application/problem+json", unauthorized.Content.Headers.ContentType?.MediaType);

        var analyst = await client.LoginAsync();
        Assert.Equal("Analyst", analyst.Role);

        var me = await client.GetFromJsonAsync<UserResponse>("/api/auth/me");
        Assert.Equal(analyst, me);

        var users = await client.GetFromJsonAsync<UserResponse[]>("/api/users");
        Assert.NotNull(users);
        var admin = Assert.Single(users, user => user.Role == "Admin");

        var createResponse = await client.PostAsJsonAsync(
            "/api/cases",
            new CreateCaseRequest(
                "Investigate duplicate webhook deliveries",
                "Webhook delivery identifiers were observed more than once in the downstream processing log.",
                "High",
                "Service Reliability",
                analyst.Id,
                DateTime.UtcNow.AddDays(5),
                ["webhook", "integrity"]));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadRequiredJsonAsync<CaseDetailResponse>();
        Assert.Equal("New", created.Status);
        Assert.Equal("High", created.Severity);
        Assert.Equal(analyst.Id, created.AssigneeId);
        Assert.Single(created.Activity);
        Assert.EndsWith(created.Id.ToString("D"), createResponse.Headers.Location?.OriginalString);

        var patchResponse = await client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/cases/{created.Id:D}")
        {
            Content = JsonContent.Create(new
            {
                status = "InProgress",
                severity = "Critical",
                assigneeId = admin.Id,
                tags = new[] { "webhook", "escalated" }
            })
        });
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
        var updated = await patchResponse.Content.ReadRequiredJsonAsync<CaseDetailResponse>();
        Assert.Equal("InProgress", updated.Status);
        Assert.Equal("Critical", updated.Severity);
        Assert.Equal(admin.Id, updated.AssigneeId);

        var commentResponse = await client.PostAsJsonAsync(
            $"/api/cases/{created.Id:D}/comments",
            new AddCommentRequest("Confirmed three duplicate delivery identifiers and escalated the investigation."));
        Assert.Equal(HttpStatusCode.Created, commentResponse.StatusCode);
        var comment = await commentResponse.Content.ReadRequiredJsonAsync<ActivityResponse>();
        Assert.Equal("CommentAdded", comment.EventType);

        var evidenceHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes("duplicate-webhook-evidence")))
            .ToLowerInvariant();
        var evidenceResponse = await client.PostAsJsonAsync(
            $"/api/cases/{created.Id:D}/evidence",
            new AddEvidenceRequest("delivery-log.json", 4_096, "application/json", evidenceHash));
        Assert.Equal(HttpStatusCode.Created, evidenceResponse.StatusCode);
        var evidence = await evidenceResponse.Content.ReadRequiredJsonAsync<EvidenceResponse>();
        Assert.Equal(evidenceHash, evidence.Sha256);

        var detail = await client.GetFromJsonAsync<CaseDetailResponse>($"/api/cases/{created.Id:D}");
        Assert.NotNull(detail);
        Assert.Contains(detail.Evidence, item => item.Id == evidence.Id);
        Assert.Equal(4, detail.Activity.Count);

        var collection = await client.GetFromJsonAsync<CaseCollectionResponse>(
            "/api/cases?search=duplicate%20webhook");
        Assert.NotNull(collection);
        Assert.Equal(1, collection.Total);
        Assert.Equal(created.Id, Assert.Single(collection.Items).Id);

        var verification = await client.GetFromJsonAsync<AuditVerificationResponse>(
            $"/api/cases/{created.Id:D}/audit/verify");
        Assert.NotNull(verification);
        Assert.True(verification.Valid);
        Assert.Equal(4, verification.CheckedEvents);
        Assert.Null(verification.BrokenAt);

        var audit = await client.GetFromJsonAsync<AuditCollectionResponse>(
            $"/api/cases/{created.Id:D}/audit");
        Assert.NotNull(audit);
        Assert.Equal(4, audit.Total);
        Assert.Equal(Enumerable.Range(1, 4), audit.Items.Select(item => item.Sequence));
        AssertChainIsValid(audit.Items);

        var graphQlResponse = await client.PostAsJsonAsync("/graphql", new
        {
            query = "{ dashboard { totalCases openCases criticalCases resolvedCases integrityStatus recentCases { id reference title status severity assigneeName updatedAt } } }"
        });
        graphQlResponse.EnsureSuccessStatusCode();
        using (var graphQl = JsonDocument.Parse(await graphQlResponse.Content.ReadAsStringAsync()))
        {
            var dashboard = graphQl.RootElement.GetProperty("data").GetProperty("dashboard");
            Assert.True(dashboard.GetProperty("totalCases").GetInt32() >= 6);
            Assert.Equal("verified", dashboard.GetProperty("integrityStatus").GetString());
            Assert.NotEmpty(dashboard.GetProperty("recentCases").EnumerateArray());
        }

        var analystExport = await client.GetAsync($"/api/cases/{created.Id:D}/audit/export");
        Assert.Equal(HttpStatusCode.Forbidden, analystExport.StatusCode);

        using var adminClient = factory.CreateCookieClient();
        await adminClient.LoginAsync("admin@caseledger.dev", "Admin123!");
        var exportResponse = await adminClient.GetAsync($"/api/cases/{created.Id:D}/audit/export");
        exportResponse.EnsureSuccessStatusCode();
        Assert.Equal("attachment", exportResponse.Content.Headers.ContentDisposition?.DispositionType);
        var export = await exportResponse.Content.ReadRequiredJsonAsync<AuditExportResponse>();
        Assert.Equal(created.Id, export.CaseId);
        Assert.Equal(4, export.Events.Count);
        Assert.Equal(audit.Items.Select(item => item.Hash), export.Events.Select(item => item.Hash));
    }

    [Fact]
    public async Task ValidationUsesProblemDetails()
    {
        using var factory = new CaseLedgerFactory();
        using var client = factory.CreateCookieClient();
        await client.LoginAsync();

        var response = await client.PostAsJsonAsync("/api/cases", new
        {
            title = "x",
            summary = "short",
            severity = "Urgent",
            category = ""
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = problem.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("title", out _));
        Assert.True(errors.TryGetProperty("severity", out _));
    }

    private static void AssertChainIsValid(IReadOnlyList<AuditEventResponse> events)
    {
        var previousHash = AuditChainService.GenesisHash;
        foreach (var auditEvent in events)
        {
            Assert.Equal(previousHash, auditEvent.PreviousHash);
            var input = Encoding.UTF8.GetBytes($"{previousHash}\n{auditEvent.CanonicalData}");
            var expected = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            Assert.Equal(expected, auditEvent.Hash);
            previousHash = auditEvent.Hash;
        }
    }
}
