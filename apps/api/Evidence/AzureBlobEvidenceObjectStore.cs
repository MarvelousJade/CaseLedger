using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace CaseLedger.Api.EvidenceStorage;

public sealed class AzureBlobEvidenceObjectStore : IEvidenceObjectStore
{
    private readonly BlobContainerClient container;

    public AzureBlobEvidenceObjectStore(EvidenceStorageRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Provider != EvidenceStorageProviderKind.AzureBlob ||
            options.AzureBlobAccountUri is null ||
            string.IsNullOrWhiteSpace(options.AzureBlobContainerName))
        {
            throw new ArgumentException(
                "Validated Azure Blob evidence storage settings are required.",
                nameof(options));
        }

        var credentialOptions = new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = options.ManagedIdentityClientId
        };
        var serviceClient = new BlobServiceClient(
            options.AzureBlobAccountUri,
            new DefaultAzureCredential(credentialOptions),
            new BlobClientOptions
            {
                Diagnostics = { ApplicationId = "caseledger-api" }
            });
        container = serviceClient.GetBlobContainerClient(options.AzureBlobContainerName);
    }

    public async Task WriteAsync(
        EvidenceObjectWriteRequest request,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);

        await container.CreateIfNotExistsAsync(
            PublicAccessType.None,
            cancellationToken: cancellationToken);
        var blob = container.GetBlobClient(request.ObjectId.ObjectKey);
        await blob.UploadAsync(
            content,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = request.MediaType
                },
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sha256"] = request.Sha256,
                    ["sizeBytes"] = request.SizeBytes.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                },
                Conditions = new BlobRequestConditions
                {
                    IfNoneMatch = ETag.All
                }
            },
            cancellationToken);
    }

    public async Task DeleteIfExistsAsync(
        EvidenceObjectId objectId,
        CancellationToken cancellationToken)
    {
        await container
            .GetBlobClient(objectId.ObjectKey)
            .DeleteIfExistsAsync(
                DeleteSnapshotsOption.IncludeSnapshots,
                cancellationToken: cancellationToken);
    }
}
