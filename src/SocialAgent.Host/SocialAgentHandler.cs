using System.Reflection;
using System.Text.Json;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Hosting.A2A;
using Microsoft.Agents.Hosting.A2A.Protocol;
using SocialAgent.Core.Analytics;
using SocialAgent.Core.Providers;
using SocialAgent.Data.Repositories;

namespace SocialAgent.Host;

public class SocialAgentHandler : AgentApplication, IAgentCardHandler
{
    private static readonly JsonSerializerOptions JsonPretty = new() { WriteIndented = true };
    private readonly IServiceScopeFactory _scopeFactory;

    public SocialAgentHandler(AgentApplicationOptions options, IServiceScopeFactory scopeFactory) : base(options)
    {
        _scopeFactory = scopeFactory;
        OnActivity(ActivityTypes.Message, OnMessageAsync);
        OnActivity(ActivityTypes.EndOfConversation, OnEndOfConversationAsync);
    }

    public Task<AgentCard> GetAgentCard(AgentCard initialCard)
    {
        initialCard.Name = "SocialAgent";
        initialCard.Description = "Social media monitoring and analytics agent. " +
            "Monitors Mastodon, Bluesky, and other platforms for posts, mentions, and engagement. " +
            "Provides analytics on engagement trends, top posts, and follower insights.";
        initialCard.Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        initialCard.Skills =
        [
            new AgentSkill
            {
                Id = "engagement-summary",
                Name = "Engagement Summary",
                Description = "Get a summary of recent engagement across all connected social media platforms. " +
                    "Returns total likes, reposts, replies, mentions, new followers, and averages per post.",
                Tags = ["social", "analytics", "engagement"]
            },
            new AgentSkill
            {
                Id = "top-posts",
                Name = "Top Posts",
                Description = "Get your most-engaged posts over a configurable time period, ranked by total engagement (likes + reposts + replies).",
                Tags = ["social", "analytics", "posts"]
            },
            new AgentSkill
            {
                Id = "recent-mentions",
                Name = "Recent Mentions",
                Description = "Get recent mentions and replies across all connected platforms.",
                Tags = ["social", "mentions", "notifications"]
            },
            new AgentSkill
            {
                Id = "follower-insights",
                Name = "Follower Insights",
                Description = "See who engages most with your content and their interaction patterns.",
                Tags = ["social", "analytics", "followers"]
            },
            new AgentSkill
            {
                Id = "platform-comparison",
                Name = "Platform Comparison",
                Description = "Compare engagement metrics across all connected social media platforms.",
                Tags = ["social", "analytics", "comparison"]
            },
            new AgentSkill
            {
                Id = "check-notifications",
                Name = "Check Notifications",
                Description = "Get unread notifications across all connected platforms.",
                Tags = ["social", "notifications"]
            },
            new AgentSkill
            {
                Id = "provider-status",
                Name = "Provider Status",
                Description = "Check connectivity and health of all configured social media providers.",
                Tags = ["social", "status", "health"]
            }
        ];
        return Task.FromResult(initialCard);
    }

    private async Task OnMessageAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken ct)
    {
        var text = turnContext.Activity.Text?.Trim() ?? string.Empty;
        var skillId = ExtractSkillId(text);

        using var scope = _scopeFactory.CreateScope();

        var result = skillId switch
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

        var activity = new Activity
        {
            Text = result,
            Type = ActivityTypes.EndOfConversation,
            Code = EndOfConversationCodes.CompletedSuccessfully
        };
        await turnContext.SendActivityAsync(activity, ct);
    }

    private Task OnEndOfConversationAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    private static string ExtractSkillId(string text)
    {
        var lower = text.ToLowerInvariant();
        string[] skillIds = ["engagement-summary", "top-posts", "recent-mentions",
            "follower-insights", "platform-comparison", "check-notifications", "provider-status"];
        return skillIds.FirstOrDefault(s => lower.Contains(s)) ?? lower;
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

    private static string FormatJson(object data)
    {
        return JsonSerializer.Serialize(data, JsonPretty);
    }
}
