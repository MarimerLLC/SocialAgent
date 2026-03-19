using SocialAgent.Data.Repositories;

namespace SocialAgent.Host.Services;

public class DataRetentionService(
    IServiceScopeFactory scopeFactory,
    ILogger<DataRetentionService> logger,
    IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retentionDays = configuration.GetValue("SocialAgent:RetentionDays", 30);
        var intervalHours = configuration.GetValue("SocialAgent:RetentionCheckIntervalHours", 24);

        logger.LogInformation(
            "Data retention service starting: {RetentionDays} day retention, checking every {Interval}h",
            retentionDays, intervalHours);

        // Delay startup to let migrations complete
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PurgeOldDataAsync(retentionDays, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error during data retention purge");
            }

            await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken);
        }
    }

    private async Task PurgeOldDataAsync(int retentionDays, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);

        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISocialDataRepository>();

        var postsDeleted = await repository.PurgeOldPostsAsync(cutoff, ct);
        var notificationsDeleted = await repository.PurgeOldNotificationsAsync(cutoff, ct);

        if (postsDeleted > 0 || notificationsDeleted > 0)
        {
            logger.LogInformation(
                "Data retention purge: deleted {Posts} posts and {Notifications} notifications older than {Days} days",
                postsDeleted, notificationsDeleted, retentionDays);
        }
    }
}
