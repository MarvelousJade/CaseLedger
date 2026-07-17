using Microsoft.AspNetCore.Http.Features;

namespace CaseLedger.Api.EvidenceStorage;

public static class EvidenceStorageServiceCollectionExtensions
{
    public static IServiceCollection AddEvidenceStorage(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var configured = configuration
            .GetSection(EvidenceStorageOptions.SectionName)
            .Get<EvidenceStorageOptions>() ?? new EvidenceStorageOptions();
        var options = EvidenceStorageRuntimeOptions.Validate(
            configured,
            environment.ContentRootPath);

        services.AddSingleton(options);
        services.Configure<FormOptions>(formOptions =>
        {
            formOptions.MultipartBodyLengthLimit = options.MultipartBodyLengthLimit;
        });
        services.AddSingleton<IEvidenceObjectStore>(serviceProvider =>
            options.Provider switch
            {
                EvidenceStorageProviderKind.Local =>
                    new LocalEvidenceObjectStore(options),
                EvidenceStorageProviderKind.AzureBlob =>
                    new AzureBlobEvidenceObjectStore(options),
                _ => throw new InvalidOperationException(
                    $"Unsupported evidence storage provider '{options.Provider}'.")
            });
        services.AddSingleton<EvidenceUploadService>();

        return services;
    }
}
