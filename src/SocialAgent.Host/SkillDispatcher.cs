using System.Text.Json;
using SocialAgent.Core.Analytics;
using SocialAgent.Core.Providers;
using SocialAgent.Data.Repositories;
using SocialAgent.Host.Routing;

namespace SocialAgent.Host;

internal sealed class SkillDispatcher(IServiceScopeFactory scopeFactory, ILogger<SkillDispatcher> logger)
{
    private static readonly JsonSerializerOptions JsonPretty = new() { WriteIndented = true };

    public async Task<string> DispatchAsync(string? explicitSkillId, string userText, CancellationToken ct)
    {
        var text = userText?.Trim() ?? string.Empty;

        using var scope = scopeFactory.CreateScope();

        string skillId;
        if (!string.IsNullOrWhiteSpace(explicitSkillId) && SkillCatalog.IsKnownSkill(explicitSkillId))
        {
            skillId = explicitSkillId;
            logger.LogInformation("Dispatching skill {SkillId} (from request metadata)", skillId);
        }
        else
        {
            var router = scope.ServiceProvider.GetService<SkillRouter>();
            skillId = router != null
                ? await router.RouteAsync(text, SkillCatalog.RouterDefinitions, ct) ?? SkillCatalog.MatchSkillByKeywords(text)
                : SkillCatalog.MatchSkillByKeywords(text);
            logger.LogInformation("Dispatching skill {SkillId} (routed from text \"{Input}\")", skillId, text);
        }

        return skillId switch
        {
            "engagement-summary" => await HandleEngagementSummaryAsync(scope.ServiceProvider, ct),
            "top-posts" => await HandleTopPostsAsync(scope.ServiceProvider, ct),
            "recent-mentions" => await HandleRecentMentionsAsync(scope.ServiceProvider, ct),
            "follower-insights" => await HandleFollowerInsightsAsync(scope.ServiceProvider, ct),
            "platform-comparison" => await HandlePlatformComparisonAsync(scope.ServiceProvider, ct),
            "check-notifications" => await HandleCheckNotificationsAsync(scope.ServiceProvider, ct),
            "provider-status" => await HandleProviderStatusAsync(scope.ServiceProvider, ct),
            _ => $"Unknown skill '{skillId}'. Available skills: engagement-summary, top-posts, recent-mentions, follower-insights, platform-comparison, check-notifications, provider-status"
        };
    }

    private static async Task<string> HandleEngagementSummaryAsync(IServiceProvider sp, CancellationToken ct)
    {
        var analytics = sp.GetRequiredService<IAnalyticsService>();
        var summary = await analytics.GetEngagementSummaryAsync(ct: ct);
        return FormatJson(summary);
    }

    private static async Task<string> HandleTopPostsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var analytics = sp.GetRequiredService<IAnalyticsService>();
        var posts = await analytics.GetTopPostsAsync(ct: ct);
        return FormatJson(posts);
    }

    private static async Task<string> HandleRecentMentionsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var analytics = sp.GetRequiredService<IAnalyticsService>();
        var mentions = await analytics.GetRecentMentionsAsync(ct: ct);
        return FormatJson(mentions);
    }

    private static async Task<string> HandleFollowerInsightsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var analytics = sp.GetRequiredService<IAnalyticsService>();
        var engagers = await analytics.GetTopEngagersAsync(ct: ct);
        return FormatJson(engagers);
    }

    private static async Task<string> HandlePlatformComparisonAsync(IServiceProvider sp, CancellationToken ct)
    {
        var analytics = sp.GetRequiredService<IAnalyticsService>();
        var comparison = await analytics.GetPlatformComparisonAsync(ct: ct);
        return FormatJson(comparison);
    }

    private static async Task<string> HandleCheckNotificationsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var repository = sp.GetRequiredService<ISocialDataRepository>();
        var unread = await repository.GetUnreadNotificationsAsync(ct: ct);
        return FormatJson(unread);
    }

    private static async Task<string> HandleProviderStatusAsync(IServiceProvider sp, CancellationToken ct)
    {
        var providers = sp.GetServices<ISocialMediaProvider>();
        var statuses = new List<object>();
        foreach (var provider in providers)
        {
            var connected = await provider.ValidateConnectionAsync(ct);
            statuses.Add(new
            {
                provider.ProviderId,
                provider.ProviderName,
                Connected = connected
            });
        }
        return FormatJson(statuses);
    }

    private static string FormatJson(object data) => JsonSerializer.Serialize(data, JsonPretty);
}
