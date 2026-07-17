using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using CaseLedger.Api.Contracts;
using CaseLedger.Api.EvidenceStorage;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaseLedger.Api.Tests;

public sealed class EvidenceStorageTests
{
    [Fact]
    public async Task MultipartUploadStoresBytesAndComputesSha256OnServer()
    {
        using var factory = new CaseLedgerFactory();
        using var client = factory.CreateCookieClient();
        await client.LoginAsync();
        var cases = await client.GetFromJsonAsync<CaseCollectionResponse>(
            "/api/cases?page=1&pageSize=1");
        var target = Assert.Single(Assert.IsType<CaseCollectionResponse>(cases).Items);
        var contents = Encoding.UTF8.GetBytes("server-computed evidence digest\n");

        using var multipart = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(contents);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        multipart.Add(fileContent, "file", "server-hash.txt");

        var response = await client.PostAsync(
            $"/api/cases/{target.Id:D}/evidence",
            multipart);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var evidence = await response.Content.ReadRequiredJsonAsync<EvidenceResponse>();
        var expectedSha256 = Convert
            .ToHexString(SHA256.HashData(contents))
            .ToLowerInvariant();
        Assert.Equal(expectedSha256, evidence.Sha256);
        Assert.Equal(contents.Length, evidence.SizeBytes);
        Assert.Equal("server-hash.txt", evidence.FileName);

        var objectId = new EvidenceObjectId(target.Id, evidence.Id);
        var objectPath = Path.Combine(
            factory.EvidenceStoragePath,
            "cases",
            target.Id.ToString("N"),
            "evidence",
            $"{evidence.Id:N}.blob");
        Assert.Equal(objectId.ObjectKey.Replace('/', Path.DirectorySeparatorChar) + ".blob",
            Path.GetRelativePath(factory.EvidenceStoragePath, objectPath));
        Assert.Equal(contents, await File.ReadAllBytesAsync(objectPath));
    }

    [Fact]
    public async Task MultipartUploadRejectsFileNamePathsWithoutWritingOutsideRoot()
    {
        using var factory = new CaseLedgerFactory();
        using var client = factory.CreateCookieClient();
        await client.LoginAsync();
        var cases = await client.GetFromJsonAsync<CaseCollectionResponse>(
            "/api/cases?page=1&pageSize=1");
        var target = Assert.Single(Assert.IsType<CaseCollectionResponse>(cases).Items);
        var escapedFileName = $"escaped-evidence-{Guid.NewGuid():N}.txt";
        var escapedPath = Path.Combine(
            Path.GetDirectoryName(factory.EvidenceStoragePath)!,
            escapedFileName);

        using var multipart = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent("unsafe"u8.ToArray());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        multipart.Add(fileContent, "file", $"../{escapedFileName}");

        var response = await client.PostAsync(
            $"/api/cases/{target.Id:D}/evidence",
            multipart);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(escapedPath));
        Assert.False(Directory.Exists(factory.EvidenceStoragePath));
    }

    [Fact]
    public async Task UncommittedUploadIsDeletedButCompletedUploadIsRetained()
    {
        var options = new EvidenceStorageRuntimeOptions(
            EvidenceStorageProviderKind.Local,
            1024,
            Path.GetTempPath(),
            null,
            null,
            null);
        var store = new TrackingObjectStore();
        var uploads = new EvidenceUploadService(
            store,
            options,
            NullLogger<EvidenceUploadService>.Instance);
        var firstId = new EvidenceObjectId(Guid.NewGuid(), Guid.NewGuid());

        await using (var uncommitted = await uploads.StageAsync(
            firstId,
            "application/octet-stream",
            new MemoryStream([1, 2, 3]),
            CancellationToken.None))
        {
        }

        Assert.Equal(firstId, Assert.Single(store.Deleted));

        var secondId = new EvidenceObjectId(Guid.NewGuid(), Guid.NewGuid());
        await using (var completed = await uploads.StageAsync(
            secondId,
            "application/octet-stream",
            new MemoryStream([4, 5, 6]),
            CancellationToken.None))
        {
            completed.Complete();
        }

        Assert.DoesNotContain(secondId, store.Deleted);
    }

    [Fact]
    public async Task UploadLimitIsEnforcedBeforeObjectWrite()
    {
        var options = new EvidenceStorageRuntimeOptions(
            EvidenceStorageProviderKind.Local,
            2,
            Path.GetTempPath(),
            null,
            null,
            null);
        var store = new TrackingObjectStore();
        var uploads = new EvidenceUploadService(
            store,
            options,
            NullLogger<EvidenceUploadService>.Instance);

        var exception = await Assert.ThrowsAsync<EvidenceUploadTooLargeException>(() =>
            uploads.StageAsync(
                new EvidenceObjectId(Guid.NewGuid(), Guid.NewGuid()),
                "application/octet-stream",
                new MemoryStream([1, 2, 3]),
                CancellationToken.None));

        Assert.Equal(2, exception.MaxFileSizeBytes);
        Assert.Equal(0, store.WriteCount);
        Assert.Empty(store.Deleted);
    }

    [Fact]
    public void AzureBlobConfigurationRequiresIdentityBasedHttpsEndpoint()
    {
        var configured = new EvidenceStorageOptions
        {
            Provider = "AzureBlob",
            AzureBlob = new AzureBlobEvidenceStorageOptions
            {
                AccountUri = "https://caseledger.blob.core.windows.net",
                ContainerName = "caseledger-evidence",
                ManagedIdentityClientId = "11111111-1111-1111-1111-111111111111"
            }
        };

        var options = EvidenceStorageRuntimeOptions.Validate(
            configured,
            AppContext.BaseDirectory);

        Assert.Equal(EvidenceStorageProviderKind.AzureBlob, options.Provider);
        Assert.Equal(
            new Uri("https://caseledger.blob.core.windows.net"),
            options.AzureBlobAccountUri);
        Assert.Equal(
            "11111111-1111-1111-1111-111111111111",
            options.ManagedIdentityClientId);
    }

    private sealed class TrackingObjectStore : IEvidenceObjectStore
    {
        public List<EvidenceObjectId> Deleted { get; } = [];
        public int WriteCount { get; private set; }

        public async Task WriteAsync(
            EvidenceObjectWriteRequest request,
            Stream content,
            CancellationToken cancellationToken)
        {
            WriteCount++;
            using var sink = new MemoryStream();
            await content.CopyToAsync(sink, cancellationToken);
            Assert.Equal(request.SizeBytes, sink.Length);
        }

        public Task DeleteIfExistsAsync(
            EvidenceObjectId objectId,
            CancellationToken cancellationToken)
        {
            Deleted.Add(objectId);
            return Task.CompletedTask;
        }
    }
}
