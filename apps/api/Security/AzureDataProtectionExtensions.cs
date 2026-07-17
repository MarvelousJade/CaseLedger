using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;

namespace CaseLedger.Api.Security;

public static class AzureDataProtectionExtensions
{
    public static IDataProtectionBuilder AddCaseLedgerDataProtection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var builder = services
            .AddDataProtection()
            .SetApplicationName("CaseLedger");
        var options = configuration
            .GetSection(AzureDataProtectionOptions.SectionName)
            .Get<AzureDataProtectionOptions>() ?? new AzureDataProtectionOptions();
        if (!options.Enabled)
        {
            return builder;
        }

        Validate(options);
        var credentialOptions = new DefaultAzureCredentialOptions();
        if (!string.IsNullOrWhiteSpace(options.ManagedIdentityClientId))
        {
            credentialOptions.ManagedIdentityClientId =
                options.ManagedIdentityClientId;
        }

        var credential = new DefaultAzureCredential(credentialOptions);
        return builder
            .PersistKeysToAzureBlobStorage(
                new Uri(options.KeyBlobUri!, UriKind.Absolute),
                credential)
            .ProtectKeysWithAzureKeyVault(
                new Uri(options.KeyVaultKeyIdentifier!, UriKind.Absolute),
                credential);
    }

    public static void Validate(AzureDataProtectionOptions options)
    {
        if (!TryGetSafeHttpsUri(options.KeyBlobUri, out var blobUri) ||
            blobUri.Segments.Length < 3 ||
            !TryGetSafeHttpsUri(options.KeyVaultKeyIdentifier, out var keyUri) ||
            keyUri.Segments.Length != 3 ||
            !string.Equals(
                keyUri.Segments[1].Trim('/'),
                "keys",
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(keyUri.Segments[2].Trim('/')) ||
            (!string.IsNullOrWhiteSpace(options.ManagedIdentityClientId) &&
             !Guid.TryParse(options.ManagedIdentityClientId, out _)))
        {
            throw new InvalidOperationException(
                "Azure Data Protection requires a secure key blob URI, a versionless " +
                "Key Vault key identifier, and an optional managed identity client ID.");
        }
    }

    private static bool TryGetSafeHttpsUri(string? value, out Uri uri)
    {
        var valid = Uri.TryCreate(value, UriKind.Absolute, out uri!) &&
                    uri.Scheme == Uri.UriSchemeHttps &&
                    string.IsNullOrEmpty(uri.UserInfo) &&
                    string.IsNullOrEmpty(uri.Query) &&
                    string.IsNullOrEmpty(uri.Fragment);
        return valid;
    }
}
