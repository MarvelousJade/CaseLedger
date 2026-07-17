using Path = System.IO.Path;

namespace CaseLedger.Api.EvidenceStorage;

public sealed class LocalEvidenceObjectStore : IEvidenceObjectStore
{
    private const int CopyBufferSize = 128 * 1024;
    private readonly string rootPath;
    private readonly string rootPathWithSeparator;

    public LocalEvidenceObjectStore(EvidenceStorageRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        rootPath = Path.GetFullPath(options.LocalRootPath);
        rootPathWithSeparator = Path.TrimEndingDirectorySeparator(rootPath) +
            Path.DirectorySeparatorChar;
    }

    public async Task WriteAsync(
        EvidenceObjectWriteRequest request,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("Evidence content must be readable.", nameof(content));
        }

        ValidateWriteRequest(request);
        var targetPath = GetObjectPath(request.ObjectId);
        var targetDirectory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(targetDirectory);
        var temporaryPath = Path.Combine(
            targetDirectory,
            $".{request.ObjectId.EvidenceId:N}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = CopyBufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                }))
            {
                await content.CopyToAsync(output, CopyBufferSize, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            var actualLength = new FileInfo(temporaryPath).Length;
            if (actualLength != request.SizeBytes)
            {
                throw new InvalidDataException(
                    $"Evidence stream length {actualLength} did not match the validated length {request.SizeBytes}.");
            }

            File.Move(temporaryPath, targetPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task DeleteIfExistsAsync(
        EvidenceObjectId objectId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetObjectPath(objectId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string GetObjectPath(EvidenceObjectId objectId)
    {
        objectId.EnsureValid();
        var path = Path.GetFullPath(Path.Combine(
            rootPath,
            "cases",
            objectId.CaseId.ToString("N"),
            "evidence",
            $"{objectId.EvidenceId:N}.blob"));

        if (!path.StartsWith(rootPathWithSeparator, PathComparison))
        {
            throw new InvalidOperationException(
                "The generated evidence object path escaped the configured storage root.");
        }

        return path;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static void ValidateWriteRequest(EvidenceObjectWriteRequest request)
    {
        if (request.SizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Evidence size cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(request.MediaType))
        {
            throw new ArgumentException("Evidence media type is required.", nameof(request));
        }

        if (request.Sha256.Length != 64 || !request.Sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Evidence SHA-256 must be 64 hexadecimal characters.", nameof(request));
        }
    }
}
