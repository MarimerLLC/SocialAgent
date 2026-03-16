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
        logger.LogInformation("Database ready");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
