using A2A;
using SocialAgent.Host.Routing;

namespace SocialAgent.Host;

internal static class SkillCatalog
{
    public static IReadOnlyList<AgentSkill> AgentCardSkills { get; } =
    [
        new()
        {
            Id = "engagement-summary",
            Name = "Engagement Summary",
            Description = "Get a summary of recent engagement across all connected social media platforms. " +
                "Returns total likes, reposts, replies, mentions, new followers, and averages per post.",
            Tags = ["social", "analytics", "engagement"]
        },
        new()
        {
            Id = "top-posts",
            Name = "Top Posts",
            Description = "Get your most-engaged posts over a configurable time period, ranked by total engagement (likes + reposts + replies).",
            Tags = ["social", "analytics", "posts"]
        },
        new()
        {
            Id = "recent-mentions",
            Name = "Recent Mentions",
            Description = "Get recent mentions and replies across all connected platforms.",
            Tags = ["social", "mentions", "notifications"]
        },
        new()
        {
            Id = "follower-insights",
            Name = "Follower Insights",
            Description = "See who engages most with your content and their interaction patterns.",
            Tags = ["social", "analytics", "followers"]
        },
        new()
        {
            Id = "platform-comparison",
            Name = "Platform Comparison",
            Description = "Compare engagement metrics across all connected social media platforms.",
            Tags = ["social", "analytics", "comparison"]
        },
        new()
        {
            Id = "check-notifications",
            Name = "Check Notifications",
            Description = "Get unread notifications across all connected platforms.",
            Tags = ["social", "notifications"]
        },
        new()
        {
            Id = "provider-status",
            Name = "Provider Status",
            Description = "Check connectivity and health of all configured social media providers.",
            Tags = ["social", "status", "health"]
        }
    ];

    public static IReadOnlyList<SkillRouter.SkillDefinition> RouterDefinitions { get; } =
    [
        new("engagement-summary", "Engagement Summary", "Get a summary of recent engagement across all connected social media platforms"),
        new("top-posts", "Top Posts", "Get most-engaged posts ranked by total engagement"),
        new("recent-mentions", "Recent Mentions", "Get recent mentions and replies across all connected platforms"),
        new("follower-insights", "Follower Insights", "See who engages most with your content"),
        new("platform-comparison", "Platform Comparison", "Compare engagement metrics across all connected platforms"),
        new("check-notifications", "Check Notifications", "Get unread notifications across all connected platforms"),
        new("provider-status", "Provider Status", "Check connectivity and health of all configured providers")
    ];

    private static readonly Dictionary<string, string[]> SkillKeywords = new()
    {
        ["engagement-summary"] = ["engagement summary", "engagement-summary"],
        ["top-posts"] = ["top posts", "top-posts", "most engaged", "best posts"],
        ["recent-mentions"] = ["recent mentions", "recent-mentions", "mentions"],
        ["follower-insights"] = ["follower insights", "follower-insights", "followers", "engagers"],
        ["platform-comparison"] = ["platform comparison", "platform-comparison", "compare platforms", "compare engagement"],
        ["check-notifications"] = ["check notifications", "check-notifications", "notifications", "unread"],
        ["provider-status"] = ["provider status", "provider-status", "connectivity", "health check"]
    };

    private static readonly HashSet<string> KnownSkillIds = new(StringComparer.Ordinal)
    {
        "engagement-summary",
        "top-posts",
        "recent-mentions",
        "follower-insights",
        "platform-comparison",
        "check-notifications",
        "provider-status"
    };

    public static bool IsKnownSkill(string id) => KnownSkillIds.Contains(id);

    public static string MatchSkillByKeywords(string text)
    {
        var lower = text.ToLowerInvariant();

        foreach (var skill in SkillKeywords)
        {
            if (lower == skill.Key)
                return skill.Key;
        }

        foreach (var skill in SkillKeywords)
        {
            foreach (var keyword in skill.Value)
            {
                if (lower.Contains(keyword))
                    return skill.Key;
            }
        }

        return lower;
    }
}
