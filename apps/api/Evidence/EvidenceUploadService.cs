using System.Buffers;
using System.Security.Cryptography;
using Path = System.IO.Path;

namespace CaseLedger.Api.EvidenceStorage;

public sealed class EvidenceUploadService(
    IEvidenceObjectStore objectStore,
    EvidenceStorageRuntimeOptions options,
    ILogger<EvidenceUploadService> logger)
{
    private const int BufferSize = 128 * 1024;

    public long MaxFileSizeBytes => options.MaxFileSizeBytes;
    public long MultipartBodyLengthLimit => options.MultipartBodyLengthLimit;

    public async Task<StagedEvidenceUpload> StageAsync(
        EvidenceObjectId objectId,
        string mediaType,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        ArgumentNullException.ThrowIfNull(content);
        objectId.EnsureValid();

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "caseledger-evidence-uploads");
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryPath = Path.Combine(
            temporaryDirectory,
            $"{Guid.NewGuid():N}.tmp");
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            await using var stagedContent = new FileStream(
                temporaryPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    BufferSize = BufferSize,
                    Options = FileOptions.Asynchronous |
                              FileOptions.SequentialScan |
                              FileOptions.DeleteOnClose
                });
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long sizeBytes = 0;

            while (true)
            {
                var count = await content.ReadAsync(
                    buffer.AsMemory(0, BufferSize),
                    cancellationToken);
                if (count == 0)
                {
                    break;
                }

                sizeBytes = checked(sizeBytes + count);
                if (sizeBytes > options.MaxFileSizeBytes)
                {
                    throw new EvidenceUploadTooLargeException(options.MaxFileSizeBytes);
                }

                hasher.AppendData(buffer, 0, count);
                await stagedContent.WriteAsync(
                    buffer.AsMemory(0, count),
                    cancellationToken);
            }

            await stagedContent.FlushAsync(cancellationToken);
            stagedContent.Position = 0;
            var sha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            var writeRequest = new EvidenceObjectWriteRequest(
                objectId,
                sizeBytes,
                mediaType,
                sha256);
            await objectStore.WriteAsync(writeRequest, stagedContent, cancellationToken);

            return new StagedEvidenceUpload(
                objectStore,
                objectId,
                sizeBytes,
                sha256,
                logger);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

public sealed class StagedEvidenceUpload : IAsyncDisposable
{
    private readonly IEvidenceObjectStore objectStore;
    private readonly ILogger logger;
    private int state;

    internal StagedEvidenceUpload(
        IEvidenceObjectStore objectStore,
        EvidenceObjectId objectId,
        long sizeBytes,
        string sha256,
        ILogger logger)
    {
        this.objectStore = objectStore;
        this.logger = logger;
        ObjectId = objectId;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
    }

    public EvidenceObjectId ObjectId { get; }
    public long SizeBytes { get; }
    public string Sha256 { get; }

    public void Complete()
    {
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "The staged evidence upload is no longer pending.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref state, 2, 0) != 0)
        {
            return;
        }

        try
        {
            await objectStore.DeleteIfExistsAsync(ObjectId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to remove uncommitted evidence object {EvidenceObjectKey}.",
                ObjectId.ObjectKey);
        }
    }
}

public sealed class EvidenceUploadTooLargeException(long maxFileSizeBytes)
    : Exception($"Evidence content exceeds the {maxFileSizeBytes} byte upload limit.")
{
    public long MaxFileSizeBytes { get; } = maxFileSizeBytes;
}
