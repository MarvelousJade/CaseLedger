using Azure.Messaging.ServiceBus;

namespace CaseLedger.Api.Messaging;

public enum MessagingProviderKind
{
    RabbitMq,
    AzureServiceBus
}

public static class MessagingRuntimeOptions
{
    public static MessagingProviderKind Validate(MessagingOptions options)
    {
        if (string.Equals(
                options.Provider,
                "RabbitMq",
                StringComparison.OrdinalIgnoreCase))
        {
            ValidateRabbitMq(options.RabbitMq);
            return MessagingProviderKind.RabbitMq;
        }

        if (string.Equals(
                options.Provider,
                "AzureServiceBus",
                StringComparison.OrdinalIgnoreCase))
        {
            ValidateAzureServiceBus(options.AzureServiceBus);
            return MessagingProviderKind.AzureServiceBus;
        }

        throw new InvalidOperationException(
            "Messaging:Provider must be RabbitMq or AzureServiceBus when messaging is enabled.");
    }

    private static void ValidateRabbitMq(RabbitMqOptions? options)
    {
        if (!Uri.TryCreate(options?.Uri, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("amqp" or "amqps"))
        {
            throw new InvalidOperationException(
                "Messaging:RabbitMq:Uri must be an absolute amqp or amqps URI.");
        }
    }

    private static void ValidateAzureServiceBus(AzureServiceBusOptions? options)
    {
        if (options is null)
        {
            throw InvalidAzureConfiguration();
        }

        var hasConnectionString =
            !string.IsNullOrWhiteSpace(options.ConnectionString);
        var hasNamespace =
            !string.IsNullOrWhiteSpace(options.FullyQualifiedNamespace);
        if (hasConnectionString == hasNamespace ||
            (hasConnectionString &&
             !string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)))
        {
            throw InvalidAzureConfiguration();
        }

        if (hasConnectionString)
        {
            ServiceBusConnectionStringProperties properties;
            try
            {
                properties = ServiceBusConnectionStringProperties.Parse(
                    options.ConnectionString);
            }
            catch (Exception exception) when (
                exception is ArgumentException or FormatException)
            {
                throw InvalidAzureConfiguration();
            }

            if (!string.IsNullOrWhiteSpace(properties.EntityPath) &&
                !string.Equals(
                    properties.EntityPath,
                    options.TopicName,
                    StringComparison.Ordinal))
            {
                throw InvalidAzureConfiguration();
            }
        }
        else if (!IsFullyQualifiedNamespace(options.FullyQualifiedNamespace) ||
                 (!string.IsNullOrWhiteSpace(options.ManagedIdentityClientId) &&
                  !Guid.TryParse(options.ManagedIdentityClientId, out _)))
        {
            throw InvalidAzureConfiguration();
        }

        if (!IsSafeEntityName(options.TopicName, maximumLength: 260) ||
            !IsSafeEntityName(options.ResultSubscriptionName, maximumLength: 50))
        {
            throw InvalidAzureConfiguration();
        }
    }

    private static bool IsFullyQualifiedNamespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 255 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            Uri.CheckHostName(value) != UriHostNameType.Dns)
        {
            return false;
        }

        return Uri.TryCreate($"sb://{value}", UriKind.Absolute, out var uri) &&
               string.Equals(uri.Host, value, StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath == "/" &&
               string.IsNullOrEmpty(uri.Query) &&
               string.IsNullOrEmpty(uri.Fragment);
    }

    private static bool IsSafeEntityName(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        value.All(character =>
            character is >= 'A' and <= 'Z' ||
            character is >= 'a' and <= 'z' ||
            character is >= '0' and <= '9' ||
            character is '.' or '-' or '_' or '/');

    private static InvalidOperationException InvalidAzureConfiguration() =>
        new(
            "Messaging:AzureServiceBus requires exactly one valid connection string " +
            "or fully qualified namespace, plus a topic and result subscription name.");
}
