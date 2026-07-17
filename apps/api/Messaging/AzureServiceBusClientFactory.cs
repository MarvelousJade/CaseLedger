using Azure.Identity;
using Azure.Messaging.ServiceBus;

namespace CaseLedger.Api.Messaging;

public static class AzureServiceBusClientFactory
{
    public static ServiceBusClient Create(AzureServiceBusOptions settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            return new ServiceBusClient(
                settings.ConnectionString,
                ClientOptions());
        }

        var credentialOptions = new DefaultAzureCredentialOptions();
        if (!string.IsNullOrWhiteSpace(settings.ManagedIdentityClientId))
        {
            credentialOptions.ManagedIdentityClientId =
                settings.ManagedIdentityClientId;
        }

        return new ServiceBusClient(
            settings.FullyQualifiedNamespace!,
            new DefaultAzureCredential(credentialOptions),
            ClientOptions());
    }

    private static ServiceBusClientOptions ClientOptions() =>
        new()
        {
            Identifier = "caseledger-api"
        };
}
