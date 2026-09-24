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
            // Only a fired stoppingToken means shutdown. An HttpClient timeout surfaces as
            // TaskCanceledException, which is an OperationCanceledException — filtering on the
            // type alone let a transient network blip escape ExecuteAsync and, via the host's
            // default StopHost behaviour, terminate the whole process.
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Error during social media polling cycle");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
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
            // As above: a timeout against one provider must not abort the cycle or the host.
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Error polling provider {ProviderId}", provider.ProviderId);
            }
        }
    }

    /// <summary>
    /// Start of the post fetch window: the engagement-refresh window, or the last poll if that is
    /// older (the pod was down longer than the window), so a gap never skips posts. Null — a
    /// first-ever poll — is passed through so providers fetch their bounded initial backfill.
    /// </summary>
    private DateTimeOffset? PostsSince(DateTimeOffset? lastPoll) =>
        PostsSince(lastPoll, configuration.GetValue("SocialAgent:EngagementRefreshDays", 7), DateTimeOffset.UtcNow);

    internal static DateTimeOffset? PostsSince(DateTimeOffset? lastPoll, int refreshDays, DateTimeOffset now)
    {
        if (lastPoll is null)
        {
            return null;
        }

        var windowStart = now.AddDays(-Math.Max(0, refreshDays));
        return lastPoll < windowStart ? lastPoll : windowStart;
    }

    private async Task PollProviderAsync(ISocialMediaProvider provider, ISocialDataRepository repository, CancellationToken ct)
    {
        logger.LogDebug("Polling {Provider}...", provider.ProviderName);

        var pollState = await repository.GetPollStateAsync(provider.ProviderId, ct);
        var since = pollState?.LastPollTime;

        // Fetch and store profile
        var profile = await provider.GetProfileAsync(ct);
        await repository.UpsertProfileAsync(profile, ct);

        // Fetch and store posts. Likes, reposts and replies keep arriving for days after a post is
        // published, so posts are re-fetched across a trailing window rather than only since the
        // last poll — fetching each post once, while it was new, froze its engagement at whatever
        // it had in its first few minutes.
        var posts = await provider.GetRecentPostsAsync(PostsSince(since), ct);
        if (posts.Count > 0)
        {
            var inserted = await repository.UpsertPostsAsync(posts, ct);
            if (inserted > 0)
            {
                logger.LogInformation("Stored {Count} new posts from {Provider}", inserted, provider.ProviderName);
            }
            logger.LogDebug("Refreshed engagement on {Count} posts from {Provider}",
                posts.Count - inserted, provider.ProviderName);
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
