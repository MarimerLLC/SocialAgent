using Microsoft.EntityFrameworkCore;
using SocialAgent.Data;

namespace SocialAgent.Host.Services;

public class DatabaseMigrationService(
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseMigrationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Applying database migrations...");
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SocialAgentDbContext>();
        await db.Database.EnsureCreatedAsync(cancellationToken);

        // EnsureCreatedAsync is a no-op on databases that already exist, so any table
        // added after the initial deployment must be patched in idempotently here.
        // This is an interim approach until we adopt EF Core migrations.
        await EnsureProviderTokensTableAsync(db, cancellationToken);

        logger.LogInformation("Database ready");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task EnsureProviderTokensTableAsync(SocialAgentDbContext db, CancellationToken ct)
    {
        var ddl = db.Database.IsNpgsql()
            ? """
              CREATE TABLE IF NOT EXISTS "ProviderTokens" (
                  "ProviderId"  text NOT NULL PRIMARY KEY,
                  "AccessToken" text NOT NULL,
                  "ExpiresAt"   timestamptz NOT NULL,
                  "UpdatedAt"   timestamptz NOT NULL
              );
              """
            : """
              CREATE TABLE IF NOT EXISTS "ProviderTokens" (
                  "ProviderId"  TEXT NOT NULL PRIMARY KEY,
                  "AccessToken" TEXT NOT NULL,
                  "ExpiresAt"   TEXT NOT NULL,
                  "UpdatedAt"   TEXT NOT NULL
              );
              """;

        await db.Database.ExecuteSqlRawAsync(ddl, ct);
        logger.LogDebug("ProviderTokens table is present");
    }
}
