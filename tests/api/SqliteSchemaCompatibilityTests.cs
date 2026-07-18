using CaseLedger.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CaseLedger.Api.Tests;

public sealed class SqliteSchemaCompatibilityTests
{
    [Fact]
    public async Task CurrentSchemaPassesCompatibilityCheck()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();

        await SqliteSchemaCompatibility.EnsureCurrentAsync(db);
    }

    [Fact]
    public async Task LegacyDatabaseFailsWithActionableResetInstructions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE Users (Id TEXT NOT NULL PRIMARY KEY);";
            await command.ExecuteNonQueryAsync();
        }

        await using var db = CreateContext(connection);
        Assert.False(await db.Database.EnsureCreatedAsync());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SqliteSchemaCompatibility.EnsureCurrentAsync(db));

        Assert.Contains("older CaseLedger schema", exception.Message);
        Assert.Contains("rename or delete", exception.Message);
        Assert.Contains("OperationalReplays", exception.Message);
        Assert.Contains("PostgreSQL", exception.Message);
    }

    [Fact]
    public async Task MissingColumnIsReportedWithItsQualifiedName()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE \"Cases\" DROP COLUMN \"Version\";");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SqliteSchemaCompatibility.EnsureCurrentAsync(db));

        Assert.Contains("columns [Cases.Version]", exception.Message);
    }

    private static CaseLedgerDbContext CreateContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<CaseLedgerDbContext>()
            .UseSqlite(connection)
            .Options;
        return new CaseLedgerDbContext(options);
    }
}
