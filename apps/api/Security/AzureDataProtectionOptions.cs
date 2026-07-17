namespace CaseLedger.Api.Security;

public sealed class AzureDataProtectionOptions
{
    public const string SectionName = "DataProtection:Azure";

    public bool Enabled { get; init; }
    public string? KeyBlobUri { get; init; }
    public string? KeyVaultKeyIdentifier { get; init; }
    public string? ManagedIdentityClientId { get; init; }
}
