using System.Text.RegularExpressions;
using Path = System.IO.Path;

namespace CaseLedger.Api.EvidenceStorage;

public sealed class EvidenceStorageOptions
{
    public const string SectionName = "EvidenceStorage";
    public const long DefaultMaxFileSizeBytes = 25 * 1024 * 1024;

    public string Provider { get; init; } = "Local";
    public long MaxFileSizeBytes { get; init; } = DefaultMaxFileSizeBytes;
    public LocalEvidenceStorageOptions Local { get; init; } = new();
    public AzureBlobEvidenceStorageOptions AzureBlob { get; init; } = new();
}

public sealed class LocalEvidenceStorageOptions
{
    public string RootPath { get; init; } = Path.Combine("App_Data", "evidence");
}

public sealed class AzureBlobEvidenceStorageOptions
{
    public string? AccountUri { get; init; }
    public string ContainerName { get; init; } = "caseledger-evidence";
    public string? ManagedIdentityClientId { get; init; }
}

public enum EvidenceStorageProviderKind
{
    Local,
    AzureBlob
}

public sealed partial record EvidenceStorageRuntimeOptions(
    EvidenceStorageProviderKind Provider,
    long MaxFileSizeBytes,
    string LocalRootPath,
    Uri? AzureBlobAccountUri,
    string? AzureBlobContainerName,
    string? ManagedIdentityClientId)
{
    private const long MultipartEnvelopeAllowanceBytes = 1024 * 1024;

    public long MultipartBodyLengthLimit =>
        MaxFileSizeBytes > long.MaxValue - MultipartEnvelopeAllowanceBytes
            ? long.MaxValue
            : MaxFileSizeBytes + MultipartEnvelopeAllowanceBytes;

    public static EvidenceStorageRuntimeOptions Validate(
        EvidenceStorageOptions options,
        string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        if (options.MaxFileSizeBytes <= 0)
        {
            throw new InvalidOperationException(
                "EvidenceStorage:MaxFileSizeBytes must be greater than zero.");
        }

        var localRootPath = ResolveLocalRoot(options.Local.RootPath, contentRootPath);
        if (options.Provider.Equals("Local", StringComparison.OrdinalIgnoreCase))
        {
            return new EvidenceStorageRuntimeOptions(
                EvidenceStorageProviderKind.Local,
                options.MaxFileSizeBytes,
                localRootPath,
                null,
                null,
                null);
        }

        if (!options.Provider.Equals("AzureBlob", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported EvidenceStorage:Provider '{options.Provider}'. Use Local or AzureBlob.");
        }

        if (!Uri.TryCreate(options.AzureBlob.AccountUri, UriKind.Absolute, out var accountUri) ||
            accountUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(accountUri.UserInfo) ||
            !string.IsNullOrEmpty(accountUri.Query) ||
            !string.IsNullOrEmpty(accountUri.Fragment))
        {
            throw new InvalidOperationException(
                "EvidenceStorage:AzureBlob:AccountUri must be an absolute HTTPS URI without credentials, query, or fragment.");
        }

        var containerName = options.AzureBlob.ContainerName.Trim();
        if (!ContainerNameRegex().IsMatch(containerName))
        {
            throw new InvalidOperationException(
                "EvidenceStorage:AzureBlob:ContainerName must be a valid lowercase Azure Blob container name.");
        }

        return new EvidenceStorageRuntimeOptions(
            EvidenceStorageProviderKind.AzureBlob,
            options.MaxFileSizeBytes,
            localRootPath,
            accountUri,
            containerName,
            NormalizeOptional(options.AzureBlob.ManagedIdentityClientId));
    }

    private static string ResolveLocalRoot(string configuredPath, string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException(
                "EvidenceStorage:Local:RootPath is required.");
        }

        var candidate = configuredPath.Trim();
        return Path.GetFullPath(
            Path.IsPathRooted(candidate)
                ? candidate
                : Path.Combine(contentRootPath, candidate));
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9]|-(?!-)){1,61}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerNameRegex();
}
