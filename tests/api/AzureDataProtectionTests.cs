using CaseLedger.Api.Security;

namespace CaseLedger.Api.Tests;

public sealed class AzureDataProtectionTests
{
    [Fact]
    public void ManagedIdentityConfigurationAcceptsVersionlessKeyIdentifier()
    {
        AzureDataProtectionExtensions.Validate(
            new AzureDataProtectionOptions
            {
                Enabled = true,
                KeyBlobUri =
                    "https://caseledger.blob.core.windows.net/keys/key-ring.xml",
                KeyVaultKeyIdentifier =
                    "https://caseledger.vault.azure.net/keys/data-protection",
                ManagedIdentityClientId =
                    "7b092ac5-4c37-4972-9aa7-50f9d39750df"
            });
    }

    [Theory]
    [InlineData("https://caseledger.blob.core.windows.net/keys/key-ring.xml?sig=private")]
    [InlineData("http://caseledger.blob.core.windows.net/keys/key-ring.xml")]
    public void KeyBlobUriRejectsEmbeddedCredentialsOrCleartext(string uri)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => AzureDataProtectionExtensions.Validate(
                new AzureDataProtectionOptions
                {
                    Enabled = true,
                    KeyBlobUri = uri,
                    KeyVaultKeyIdentifier =
                        "https://caseledger.vault.azure.net/keys/data-protection"
                }));

        Assert.DoesNotContain(uri, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void VersionedKeyVaultIdentifierIsRejected()
    {
        const string versionedKey =
            "https://caseledger.vault.azure.net/keys/data-protection/private-version";
        var exception = Assert.Throws<InvalidOperationException>(
            () => AzureDataProtectionExtensions.Validate(
                new AzureDataProtectionOptions
                {
                    Enabled = true,
                    KeyBlobUri =
                        "https://caseledger.blob.core.windows.net/keys/key-ring.xml",
                    KeyVaultKeyIdentifier = versionedKey
                }));

        Assert.DoesNotContain(
            versionedKey,
            exception.ToString(),
            StringComparison.Ordinal);
    }
}
