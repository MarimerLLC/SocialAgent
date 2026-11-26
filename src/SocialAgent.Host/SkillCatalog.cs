using A2A;
using SocialAgent.Host.Routing;

namespace SocialAgent.Host;

internal static class SkillCatalog
{
    // How every skill that filters by provider describes the parameter. Callers read this from the
    // agent card, so it says plainly that one call covers every provider: RockBot had concluded
    // that combined requests fail and was querying one provider at a time (#9).
    private const string ProviderIdParameter =
        "providerId (string; one provider, case-insensitive, e.g. \"mastodon\" or \"bluesky\". " +
        "Omit it, or pass \"all\", to cover every provider in a single call; there is no need to query providers one at a time. " +
        "A list naming every provider, as a comma-separated string or a JSON array, also means all of them. " +
        "An unknown value returns a JSON object {\"error\": ..., \"validProviderIds\": [...]} instead of data)";

    // The skills whose handlers filter by provider. platform-comparison is all-platforms by design,
    // and provider-status reports every provider, so neither takes providerId.
    private static readonly HashSet<string> ProviderFilteredSkillIds = new(StringComparer.Ordinal)
    {
        "engagement-summary",
        "top-posts",
        "recent-mentions",
        "follower-insights",
        "check-notifications"
    };

    public static bool TakesProviderId(string skillId) => ProviderFilteredSkillIds.Contains(skillId);

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
                "Optional metadata parameters: " + ProviderIdParameter + ", since (ISO-8601 timestamp; default: last 7 days).",
            Tags = ["social", "analytics", "engagement"]
        },
        new()
        {
            Id = "top-posts",
            Name = "Top Posts",
            Description = "Get your most-engaged posts ranked by total engagement (likes + reposts + replies). " +
                "Optional metadata parameters: " + ProviderIdParameter + ", count (integer, default 10), since (ISO-8601 timestamp).",
            Tags = ["social", "analytics", "posts"]
        },
        new()
        {
            Id = "recent-mentions",
            Name = "Recent Mentions",
            Description = "Get recent mentions and replies, newest first. " +
                "Optional metadata parameters: " + ProviderIdParameter + ", count (integer, default 20), " +
                "since (ISO-8601 timestamp; only mentions and replies at or after it; default: no limit).",
            Tags = ["social", "mentions", "notifications"]
        },
        new()
        {
            Id = "follower-insights",
            Name = "Follower Insights",
            Description = "See who engages most with your content and their interaction patterns. " +
                "Optional metadata parameters: " + ProviderIdParameter + ", count (integer, default 10), since (ISO-8601 timestamp).",
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
                "Optional metadata parameters: " + ProviderIdParameter + ".",
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
