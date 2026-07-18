using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CaseLedger.Api.Data;

public static class SqliteSchemaCompatibility
{
    public static async Task EnsureCurrentAsync(
        CaseLedgerDbContext db,
        CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsSqlite())
        {
            return;
        }

        var expectedTables = db.Model.GetRelationalModel().Tables
            .ToDictionary(
                table => table.Name,
                table => table.Columns
                    .Select(column => column.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var actualTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT name FROM sqlite_schema " +
                    "WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    actualTables.Add(reader.GetString(0));
                }
            }

            var missingTables = expectedTables.Keys
                .Where(table => !actualTables.Contains(table))
                .Order(StringComparer.Ordinal)
                .ToArray();
            var missingColumns = new List<string>();

            foreach (var (table, expectedColumns) in expectedTables
                         .Where(item => actualTables.Contains(item.Key))
                         .OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                var actualColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT name FROM pragma_table_info($tableName);";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "$tableName";
                parameter.Value = table;
                command.Parameters.Add(parameter);

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    actualColumns.Add(reader.GetString(0));
                }

                missingColumns.AddRange(expectedColumns
                    .Where(column => !actualColumns.Contains(column))
                    .Order(StringComparer.Ordinal)
                    .Select(column => $"{table}.{column}"));
            }

            if (missingTables.Length == 0 && missingColumns.Count == 0)
            {
                return;
            }

            var missingObjects = new List<string>();
            if (missingTables.Length > 0)
            {
                missingObjects.Add($"tables [{string.Join(", ", missingTables)}]");
            }

            if (missingColumns.Count > 0)
            {
                missingObjects.Add($"columns [{string.Join(", ", missingColumns)}]");
            }

            throw new InvalidOperationException(
                "The SQLite database was created by an older CaseLedger schema, and local " +
                "SQLite databases are not migrated in place. Back up any data you need, stop " +
                "CaseLedger, rename or delete the configured SQLite database file " +
                "(caseledger.db by default), and restart to create the current schema. Use " +
                "PostgreSQL for durable environments that require in-place schema upgrades. " +
                $"Missing required schema objects: {string.Join("; ", missingObjects)}.");
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }
}
