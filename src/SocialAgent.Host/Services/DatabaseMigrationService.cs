using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using SocialAgent.Data;

namespace SocialAgent.Host.Services;

/// <summary>
/// Brings the database up to the current model using EF Core migrations.
///
/// Databases created before 1.5.0 were provisioned by <c>EnsureCreatedAsync</c> and have no
/// <c>__EFMigrationsHistory</c>. Running the migrations against one would fail on "table already
/// exists", so such a database is adopted instead: each migration whose tables are already present is
/// recorded as applied, and <c>MigrateAsync</c> then creates whatever is genuinely missing. That
/// matters because the fleet is not uniform — a 1.3.x database lacks <c>ProviderTokens</c>, which a
/// 1.4.0 database has.
/// </summary>
public class DatabaseMigrationService(
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseMigrationService> logger) : IHostedService
{
    /// <summary>EF Core's default migrations-history table; this project does not rename it.</summary>
    private const string HistoryTableName = "__EFMigrationsHistory";

    /// <summary>
    /// The table each pre-1.5.0 migration introduced, keyed by migration name (the id minus its
    /// timestamp). Adoption stamps a migration only when its table already exists. Migrations added
    /// from 1.5.0 on never need an entry: every 1.5.0+ database has a history table.
    /// </summary>
    private static readonly (string Migration, string Table)[] LegacyMigrations =
    [
        ("InitialCreate", "Posts"),
        ("AddProviderTokens", "ProviderTokens"),
    ];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SocialAgentDbContext>();

        // Deliberately not IHistoryRepository.ExistsAsync: on Npgsql it reports true even when no
        // __EFMigrationsHistory table is present, which would skip adoption and send MigrateAsync
        // at a schema that already exists. Querying the catalogue directly is also quiet — EF logs
        // its own probe of a missing history table at Error level, which is alarming on a first run.
        if (await db.GetService<IRelationalDatabaseCreator>().ExistsAsync(cancellationToken))
        {
            var historyExists = await TableExistsAsync(db, HistoryTableName, cancellationToken);
            if (!historyExists && await TableExistsAsync(db, "Posts", cancellationToken))
            {
                await AdoptLegacyDatabaseAsync(db, db.GetService<IHistoryRepository>(), cancellationToken);
            }
            else if (historyExists)
            {
                var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
                if (pending.Count == 0)
                {
                    logger.LogInformation("Database is up to date; no migrations to apply");
                    return;
                }
                logger.LogInformation("Applying {Count} database migration(s): {Migrations}",
                    pending.Count, string.Join(", ", pending));
            }
        }

        await db.Database.MigrateAsync(cancellationToken);
        logger.LogInformation("Database ready");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Whether <paramref name="tableName"/> exists, asked of the database catalogue directly.
    /// Adoption tests for <c>Posts</c> specifically rather than "has any table", so pointing at an
    /// unrelated populated database is not mistaken for a pre-1.5.0 SocialAgent database and stamped.
    /// </summary>
    private static async Task<bool> TableExistsAsync(
        SocialAgentDbContext db, string tableName, CancellationToken ct)
    {
        var sql = db.Database.IsNpgsql()
            ? """
              SELECT COUNT(*) FROM information_schema.tables
              WHERE table_schema = current_schema() AND table_name = @name
              """
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name";

        var connection = db.Database.GetDbConnection();
        var opened = false;
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
            opened = true;
        }

        try
        {
            using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@name";
            parameter.Value = tableName;
            command.Parameters.Add(parameter);

            var result = await command.ExecuteScalarAsync(ct);
            return Convert.ToInt64(result) > 0;
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task AdoptLegacyDatabaseAsync(
        SocialAgentDbContext db, IHistoryRepository history, CancellationToken ct)
    {
        var migrations = db.Database.GetMigrations().ToList();
        var toStamp = new List<string>();

        foreach (var (name, table) in LegacyMigrations)
        {
            var id = migrations.FirstOrDefault(m => m.EndsWith("_" + name, StringComparison.Ordinal));
            if (id is null)
            {
                logger.LogWarning("Migration {Migration} not found in the migrations assembly; not stamping it", name);
                continue;
            }
            if (await TableExistsAsync(db, table, ct))
            {
                toStamp.Add(id);
            }
        }

        if (toStamp.Count == 0)
        {
            return;
        }

        logger.LogInformation(
            "Existing database has no migrations history; adopting it by recording {Migrations} as applied",
            string.Join(", ", toStamp));

        // EF Core's own version string, recorded as metadata in the history table.
        var productVersion = typeof(DbContext).Assembly.GetName().Version?.ToString(3) ?? "10.0.0";

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct);
            foreach (var id in toStamp)
            {
                await db.Database.ExecuteSqlRawAsync(
                    history.GetInsertScript(new HistoryRow(id, productVersion)), ct);
            }
            await transaction.CommitAsync(ct);
        });

        logger.LogInformation("Adoption recorded; any remaining migrations will now apply");
    }
}
