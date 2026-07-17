using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseLedger.Api.Contracts;

namespace CaseLedger.Api.Tests;

public sealed class ApiContractTests
{
    [Fact]
    public async Task SwaggerDocumentsTheRestContract()
    {
        using var factory = new CaseLedgerFactory();
        using var client = factory.CreateClient();

        var documentResponse = await client.GetAsync("/swagger/v1/swagger.json");
        documentResponse.EnsureSuccessStatusCode();
        Assert.Equal("application/json", documentResponse.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await documentResponse.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("CaseLedger API", root.GetProperty("info").GetProperty("title").GetString());

        var paths = root.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/cases", out var casesPath));
        Assert.True(casesPath.TryGetProperty("get", out var listOperation));
        Assert.True(casesPath.TryGetProperty("post", out _));
        Assert.Contains(
            listOperation.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "page");
        Assert.Contains(
            listOperation.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "pageSize");

        var casePath = paths.GetProperty("/api/cases/{id}");
        var patchOperation = casePath.GetProperty("patch");
        Assert.Contains(
            patchOperation.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "If-Match");
        var responses = patchOperation.GetProperty("responses");
        Assert.True(responses.TryGetProperty("412", out _));
        Assert.True(responses.TryGetProperty("428", out _));

        var evidenceOperation = paths
            .GetProperty("/api/cases/{id}/evidence")
            .GetProperty("post");
        var evidenceRequestBody = evidenceOperation.GetProperty("requestBody");
        Assert.Contains(
            "compatibility-only",
            evidenceRequestBody.GetProperty("description").GetString());
        var evidenceContent = evidenceRequestBody.GetProperty("content");
        var uploadFileSchema = evidenceContent
            .GetProperty("multipart/form-data")
            .GetProperty("schema")
            .GetProperty("properties")
            .GetProperty("file");
        Assert.Equal("string", uploadFileSchema.GetProperty("type").GetString());
        Assert.Equal("binary", uploadFileSchema.GetProperty("format").GetString());
        Assert.True(evidenceContent
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("properties")
            .TryGetProperty("sha256", out _));

        var schemas = root.GetProperty("components").GetProperty("schemas");
        var collectionSchema = schemas.GetProperty(nameof(CaseCollectionResponse));
        var collectionProperties = collectionSchema.GetProperty("properties");
        Assert.True(collectionProperties.TryGetProperty("page", out _));
        Assert.True(collectionProperties.TryGetProperty("pageSize", out _));
        Assert.True(collectionProperties.TryGetProperty("totalPages", out _));
        Assert.True(collectionProperties.TryGetProperty("hasNextPage", out _));
        Assert.True(collectionProperties.TryGetProperty("hasPreviousPage", out _));

        var uiResponse = await client.GetAsync("/swagger/index.html");
        uiResponse.EnsureSuccessStatusCode();
        Assert.Equal("text/html", uiResponse.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task CaseListUsesStablePageMetadataAndPreservesLegacyPaging()
    {
        using var factory = new CaseLedgerFactory();
        using var client = factory.CreateCookieClient();
        await client.LoginAsync();

        var first = await client.GetFromJsonAsync<CaseCollectionResponse>(
            "/api/cases?page=1&pageSize=2");
        Assert.NotNull(first);
        Assert.Equal(5, first.Total);
        Assert.Equal(1, first.Page);
        Assert.Equal(2, first.PageSize);
        Assert.Equal(3, first.TotalPages);
        Assert.True(first.HasNextPage);
        Assert.False(first.HasPreviousPage);
        Assert.Equal(["CL-2026-004", "CL-2026-002"], first.Items.Select(item => item.Reference));
        Assert.All(first.Items, item => Assert.NotEqual(Guid.Empty, item.Version));

        var second = await client.GetFromJsonAsync<CaseCollectionResponse>(
            "/api/cases?page=2&pageSize=2");
        Assert.NotNull(second);
        Assert.Equal(["CL-2026-001", "CL-2026-003"], second.Items.Select(item => item.Reference));
        Assert.True(second.HasNextPage);
        Assert.True(second.HasPreviousPage);

        var last = await client.GetFromJsonAsync<CaseCollectionResponse>(
            "/api/cases?page=3&pageSize=2");
        Assert.NotNull(last);
        Assert.Equal("CL-2026-005", Assert.Single(last.Items).Reference);
        Assert.False(last.HasNextPage);
        Assert.True(last.HasPreviousPage);

        var legacy = await client.GetFromJsonAsync<CaseCollectionResponse>(
            "/api/cases?offset=1&limit=2");
        Assert.NotNull(legacy);
        Assert.Equal(["CL-2026-002", "CL-2026-001"], legacy.Items.Select(item => item.Reference));
        Assert.True(legacy.HasPreviousPage);

        var mixed = await client.GetAsync("/api/cases?page=1&limit=2");
        Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);

        var undefinedStatus = await client.GetAsync("/api/cases?status=999");
        Assert.Equal(HttpStatusCode.BadRequest, undefinedStatus.StatusCode);
    }

    [Fact]
    public async Task PatchRequiresCurrentStrongETagAndRejectsStaleWrites()
    {
        using var factory = new CaseLedgerFactory();
        using var client = factory.CreateCookieClient();
        await client.LoginAsync();

        var cases = await client.GetFromJsonAsync<CaseCollectionResponse>(
            "/api/cases?page=1&pageSize=1");
        var target = Assert.Single(Assert.IsType<CaseCollectionResponse>(cases).Items);

        var getResponse = await client.GetAsync($"/api/cases/{target.Id:D}");
        getResponse.EnsureSuccessStatusCode();
        var original = await getResponse.Content.ReadRequiredJsonAsync<CaseDetailResponse>();
        var originalETag = Assert.IsType<System.Net.Http.Headers.EntityTagHeaderValue>(
            getResponse.Headers.ETag);
        Assert.Equal($"\"{original.Version:D}\"", originalETag.Tag);

        var missing = await client.PatchAsJsonAsync(
            $"/api/cases/{target.Id:D}",
            new { title = "Missing precondition must not update" });
        Assert.Equal((HttpStatusCode)428, missing.StatusCode);

        using var weakRequest = new HttpRequestMessage(HttpMethod.Patch, $"/api/cases/{target.Id:D}")
        {
            Content = JsonContent.Create(new { title = "Weak precondition must not update" })
        };
        weakRequest.Headers.TryAddWithoutValidation("If-Match", $"W/\"{original.Version:D}\"");
        var weak = await client.SendAsync(weakRequest);
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        using var successfulRequest = new HttpRequestMessage(HttpMethod.Patch, $"/api/cases/{target.Id:D}")
        {
            Content = JsonContent.Create(new { title = "Updated through a current precondition" })
        };
        successfulRequest.Headers.IfMatch.Add(originalETag);
        var successful = await client.SendAsync(successfulRequest);
        successful.EnsureSuccessStatusCode();
        var updated = await successful.Content.ReadRequiredJsonAsync<CaseDetailResponse>();
        Assert.NotEqual(original.Version, updated.Version);
        Assert.Equal($"\"{updated.Version:D}\"", successful.Headers.ETag?.Tag);

        using var staleRequest = new HttpRequestMessage(HttpMethod.Patch, $"/api/cases/{target.Id:D}")
        {
            Content = JsonContent.Create(new { title = "A stale write must not win" })
        };
        staleRequest.Headers.IfMatch.Add(originalETag);
        var stale = await client.SendAsync(staleRequest);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(successful.Headers.ETag?.Tag, stale.Headers.ETag?.Tag);
        Assert.Equal("application/problem+json", stale.Content.Headers.ContentType?.MediaType);

        var current = await client.GetFromJsonAsync<CaseDetailResponse>($"/api/cases/{target.Id:D}");
        Assert.NotNull(current);
        Assert.Equal("Updated through a current precondition", current.Title);
        Assert.Equal(updated.Version, current.Version);
        Assert.Equal(original.Activity.Count + 1, current.Activity.Count);

        var verification = await client.GetFromJsonAsync<AuditVerificationResponse>(
            $"/api/cases/{target.Id:D}/audit/verify");
        Assert.NotNull(verification);
        Assert.True(verification.Valid);
    }
}
