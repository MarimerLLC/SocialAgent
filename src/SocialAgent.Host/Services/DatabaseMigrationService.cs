using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SocialAgent.Data;

namespace SocialAgent.Host.Services;

/// <summary>
/// Brings the database up to the current model using EF Core migrations.
///
/// Databases created before 1.5.0 were provisioned by <c>EnsureCreatedAsync</c> and therefore have
/// the right tables but no <c>__EFMigrationsHistory</c>. Running the baseline migration against one
/// of those would fail on "table already exists", so such a database is adopted instead: the history
/// table is created and the baseline recorded as already applied, after which later migrations run
/// normally.
/// </summary>
public class DatabaseMigrationService(
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseMigrationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SocialAgentDbContext>();

        var history = db.GetService<IHistoryRepository>();
        var historyExists = await history.ExistsAsync(cancellationToken);

        if (!historyExists && await LooksLikeLegacyDatabaseAsync(db, cancellationToken))
        {
            await AdoptLegacyDatabaseAsync(db, history, cancellationToken);
        }

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("Database is up to date; no migrations to apply");
            return;
        }

        logger.LogInformation("Applying {Count} database migration(s): {Migrations}",
            pending.Count, string.Join(", ", pending));
        await db.Database.MigrateAsync(cancellationToken);
        logger.LogInformation("Database ready");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// True when the database already holds this application's schema without a migrations history.
    /// Checks for <c>Posts</c> specifically rather than "has any table", so pointing at an unrelated
    /// populated database does not get silently stamped.
    /// </summary>
    private static async Task<bool> LooksLikeLegacyDatabaseAsync(SocialAgentDbContext db, CancellationToken ct)
    {
        var sql = db.Database.IsNpgsql()
            ? """
              SELECT COUNT(*) FROM information_schema.tables
              WHERE table_schema = current_schema() AND table_name = 'Posts'
              """
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Posts'";

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
        var baseline = db.Database.GetMigrations().FirstOrDefault();
        if (baseline is null)
        {
            logger.LogWarning("No migrations found in the configured migrations assembly; skipping baseline adoption");
            return;
        }

        logger.LogInformation(
            "Existing database has no migrations history; adopting it by recording {Baseline} as applied",
            baseline);

        // EF Core's own version string, recorded as metadata in the history table.
        var productVersion = typeof(DbContext).Assembly.GetName().Version?.ToString() ?? "10.0.0";

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct);
            await db.Database.ExecuteSqlRawAsync(
                history.GetInsertScript(new HistoryRow(baseline, productVersion)), ct);
            await transaction.CommitAsync(ct);
        });

        logger.LogInformation("Baseline recorded; later migrations will apply normally");
    }
}
