using CaseLedger.Api.Data;
using CaseLedger.Api.EvidenceStorage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CaseLedger.Api.Tests;

public sealed class CaseLedgerFactory : WebApplicationFactory<Program>
{
    private readonly bool messagingEnabled;
    private readonly string messagingProvider;
    private readonly bool demoLoginEnabled;
    private readonly IReadOnlyDictionary<string, string?> configurationOverrides;
    private readonly string databasePath = Path.Combine(
        Path.GetTempPath(),
        $"caseledger-tests-{Guid.NewGuid():N}.db");
    private readonly string evidenceStoragePath = Path.Combine(
        Path.GetTempPath(),
        $"caseledger-evidence-tests-{Guid.NewGuid():N}");

    public string EvidenceStoragePath => evidenceStoragePath;

    public CaseLedgerFactory(
        bool messagingEnabled = false,
        string messagingProvider = "RabbitMq",
        bool demoLoginEnabled = true,
        IReadOnlyDictionary<string, string?>? configurationOverrides = null)
    {
        this.messagingEnabled = messagingEnabled;
        this.messagingProvider = messagingProvider;
        this.demoLoginEnabled = demoLoginEnabled;
        this.configurationOverrides = configurationOverrides ??
            new Dictionary<string, string?>();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:CaseLedger"] = $"Data Source={databasePath}",
                ["EvidenceStorage:Provider"] = "Local",
                ["EvidenceStorage:Local:RootPath"] = evidenceStoragePath,
                ["Authentication:DemoLoginEnabled"] =
                    demoLoginEnabled.ToString(),
                ["Authentication:ShowDemoCredentials"] =
                    demoLoginEnabled.ToString(),
                ["Messaging:Enabled"] = messagingEnabled.ToString(),
                ["Messaging:Provider"] = messagingProvider
            };
            foreach (var (key, value) in configurationOverrides)
            {
                values[key] = value;
            }

            configuration.AddInMemoryCollection(values);
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<CaseLedgerDbContext>>();
            services.RemoveAll<CaseLedgerDbContext>();
            services.AddDbContext<CaseLedgerDbContext>(options =>
                options.UseSqlite($"Data Source={databasePath}"));

            services.RemoveAll<EvidenceStorageRuntimeOptions>();
            services.RemoveAll<IEvidenceObjectStore>();
            services.RemoveAll<EvidenceUploadService>();
            var evidenceOptions = new EvidenceStorageRuntimeOptions(
                EvidenceStorageProviderKind.Local,
                EvidenceStorageOptions.DefaultMaxFileSizeBytes,
                evidenceStoragePath,
                null,
                null,
                null);
            services.AddSingleton(evidenceOptions);
            services.AddSingleton<IEvidenceObjectStore>(
                new LocalEvidenceObjectStore(evidenceOptions));
            services.AddSingleton<EvidenceUploadService>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }

        if (Directory.Exists(evidenceStoragePath))
        {
            Directory.Delete(evidenceStoragePath, recursive: true);
        }
    }
}
