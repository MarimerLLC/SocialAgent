using A2A;
using SocialAgent.Host.Routing;

namespace SocialAgent.Host;

internal static class SkillCatalog
{
    // Skill parameters arrive as flat keys on message.metadata, alongside metadata.skill. Parameter
    // keys are documented per-skill below. Unknown keys are ignored. Parameter values may be JSON
    // strings, numbers, or ISO-8601 timestamps as appropriate; the dispatcher coerces strings to
    // numbers/dates where needed.
    public static IReadOnlyList<AgentSkill> AgentCardSkills { get; } =
    [
        new()
        {
            Id = "engagement-summary",
            Name = "Engagement Summary",
            Description = "Get a summary of recent engagement. Returns total likes, reposts, replies, mentions, new followers, and averages per post. " +
                "Optional metadata parameters: providerId (string, e.g. \"mastodon\" or \"bluesky\"; default: union across all providers), since (ISO-8601 timestamp; default: last 7 days).",
            Tags = ["social", "analytics", "engagement"]
        },
        new()
        {
            Id = "top-posts",
            Name = "Top Posts",
            Description = "Get your most-engaged posts ranked by total engagement (likes + reposts + replies). " +
                "Optional metadata parameters: providerId (string), count (integer, default 10), since (ISO-8601 timestamp).",
            Tags = ["social", "analytics", "posts"]
        },
        new()
        {
            Id = "recent-mentions",
            Name = "Recent Mentions",
            Description = "Get recent mentions and replies. " +
                "Optional metadata parameters: providerId (string; default: union), count (integer, default 20).",
            Tags = ["social", "mentions", "notifications"]
        },
        new()
        {
            Id = "follower-insights",
            Name = "Follower Insights",
            Description = "See who engages most with your content and their interaction patterns. " +
                "Optional metadata parameters: providerId (string), count (integer, default 10), since (ISO-8601 timestamp).",
            Tags = ["social", "analytics", "followers"]
        },
        new()
        {
            Id = "platform-comparison",
            Name = "Platform Comparison",
            Description = "Compare engagement metrics across all connected social media platforms. " +
                "Optional metadata parameters: since (ISO-8601 timestamp; default: last 7 days). This skill is always all-platforms by design.",
            Tags = ["social", "analytics", "comparison"]
        },
        new()
        {
            Id = "check-notifications",
            Name = "Check Notifications",
            Description = "Get unread notifications. " +
                "Optional metadata parameters: providerId (string; default: union).",
            Tags = ["social", "notifications"]
        },
        new()
        {
            Id = "provider-status",
            Name = "Provider Status",
            Description = "Check connectivity and health of all configured social media providers. Takes no parameters.",
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
