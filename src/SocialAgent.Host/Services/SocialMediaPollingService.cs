using SocialAgent.Core.Models;
using SocialAgent.Core.Providers;
using SocialAgent.Data.Repositories;

namespace SocialAgent.Host.Services;

public class SocialMediaPollingService(
    IServiceScopeFactory scopeFactory,
    ILogger<SocialMediaPollingService> logger,
    IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = configuration.GetValue("SocialAgent:PollingIntervalMinutes", 5);
        logger.LogInformation("Social media polling service starting with {Interval}m interval", intervalMinutes);

        // Initial delay to let the app fully start
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAllProvidersAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error during social media polling cycle");
            }

            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
        }
    }

    private async Task PollAllProvidersAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var providers = scope.ServiceProvider.GetServices<ISocialMediaProvider>();
        var repository = scope.ServiceProvider.GetRequiredService<ISocialDataRepository>();

        foreach (var provider in providers)
        {
            try
            {
                await PollProviderAsync(provider, repository, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error polling provider {ProviderId}", provider.ProviderId);
            }
        }
    }

    private async Task PollProviderAsync(ISocialMediaProvider provider, ISocialDataRepository repository, CancellationToken ct)
    {
        logger.LogDebug("Polling {Provider}...", provider.ProviderName);

        var pollState = await repository.GetPollStateAsync(provider.ProviderId, ct);
        var since = pollState?.LastPollTime;

        // Fetch and store profile
        var profile = await provider.GetProfileAsync(ct);
        await repository.UpsertProfileAsync(profile, ct);

        // Fetch and store posts
        var posts = await provider.GetRecentPostsAsync(since, ct);
        if (posts.Count > 0)
        {
            await repository.UpsertPostsAsync(posts, ct);
            logger.LogInformation("Stored {Count} posts from {Provider}", posts.Count, provider.ProviderName);
        }

        // Fetch and store notifications
        var notifications = await provider.GetNotificationsAsync(since, ct);
        if (notifications.Count > 0)
        {
            await repository.UpsertNotificationsAsync(notifications, ct);
            logger.LogInformation("Stored {Count} notifications from {Provider}", notifications.Count, provider.ProviderName);
        }

        // Update poll state
        await repository.UpsertPollStateAsync(new PollState
        {
            ProviderId = provider.ProviderId,
            LastPostId = posts.FirstOrDefault()?.PlatformPostId ?? pollState?.LastPostId,
            LastNotificationId = notifications.FirstOrDefault()?.PlatformNotificationId ?? pollState?.LastNotificationId,
            LastPollTime = DateTimeOffset.UtcNow
        }, ct);
    }
}
