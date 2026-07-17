using CaseLedger.Api.Data;
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
    private readonly string databasePath = Path.Combine(
        Path.GetTempPath(),
        $"caseledger-tests-{Guid.NewGuid():N}.db");

    public CaseLedgerFactory(
        bool messagingEnabled = false,
        string messagingProvider = "RabbitMq")
    {
        this.messagingEnabled = messagingEnabled;
        this.messagingProvider = messagingProvider;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:CaseLedger"] = $"Data Source={databasePath}",
                ["Messaging:Enabled"] = messagingEnabled.ToString(),
                ["Messaging:Provider"] = messagingProvider
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<CaseLedgerDbContext>>();
            services.RemoveAll<CaseLedgerDbContext>();
            services.AddDbContext<CaseLedgerDbContext>(options =>
                options.UseSqlite($"Data Source={databasePath}"));
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
    }
}
