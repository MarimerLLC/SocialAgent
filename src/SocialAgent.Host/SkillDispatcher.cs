using System.Globalization;
using System.Text.Json;
using SocialAgent.Core.Analytics;
using SocialAgent.Core.Providers;
using SocialAgent.Data.Repositories;
using SocialAgent.Host.Routing;

namespace SocialAgent.Host;

internal sealed class SkillDispatcher(IServiceScopeFactory scopeFactory, ILogger<SkillDispatcher> logger)
{
    private static readonly JsonSerializerOptions JsonPretty = new() { WriteIndented = true };

    public async Task<string> DispatchAsync(
        string? explicitSkillId,
        string userText,
        IReadOnlyDictionary<string, JsonElement>? parameters,
        CancellationToken ct)
    {
        var text = userText?.Trim() ?? string.Empty;

        using var scope = scopeFactory.CreateScope();

        string skillId;
        string routedFrom;
        var parameterSummary = SummarizeParameters(parameters);
        var p = new SkillParameters(parameters);

        if (!string.IsNullOrWhiteSpace(explicitSkillId) && SkillCatalog.IsKnownSkill(explicitSkillId))
        {
            skillId = explicitSkillId;
            routedFrom = "from request metadata";
        }
        else
        {
            var router = scope.ServiceProvider.GetService<SkillRouter>();
            skillId = router != null
                ? await router.RouteAsync(text, SkillCatalog.RouterDefinitions, ct) ?? SkillCatalog.MatchSkillByKeywords(text)
                : SkillCatalog.MatchSkillByKeywords(text);
            routedFrom = $"routed from text \"{text}\"";
        }

        // Resolve providerId before any query. The repository filters by exact id, so an
        // unrecognised value ("Mastodon", "all", "mastodon,bluesky") used to match nothing and
        // return an empty result that read as "no data" (#9).
        string? provider = null;
        if (SkillCatalog.TakesProviderId(skillId))
        {
            var enabledProviderIds = scope.ServiceProvider.GetServices<ISocialMediaProvider>()
                .Select(x => x.ProviderId)
                .ToList();
            var selection = ProviderSelection.Resolve(p.ProviderId, enabledProviderIds);
            if (!selection.IsValid)
            {
                logger.LogInformation("Rejected skill {SkillId} ({RoutedFrom}) parameters={Parameters}: {Error}",
                    skillId, routedFrom, parameterSummary, selection.Error);
                return selection.Error!;
            }
            provider = selection.Filter;
        }

        logger.LogInformation("Dispatching skill {SkillId} ({RoutedFrom}) parameters={Parameters} provider={Provider}",
            skillId, routedFrom, parameterSummary, provider ?? "all");

        return skillId switch
        {
            "engagement-summary" => await HandleEngagementSummaryAsync(scope.ServiceProvider, provider, p, ct),
            "top-posts" => await HandleTopPostsAsync(scope.ServiceProvider, provider, p, ct),
            "recent-mentions" => await HandleRecentMentionsAsync(scope.ServiceProvider, provider, p, ct),
            "follower-insights" => await HandleFollowerInsightsAsync(scope.ServiceProvider, provider, p, ct),
            "platform-comparison" => await HandlePlatformComparisonAsync(scope.ServiceProvider, p, ct),
            "check-notifications" => await HandleCheckNotificationsAsync(scope.ServiceProvider, provider, ct),
            "provider-status" => await HandleProviderStatusAsync(scope.ServiceProvider, ct),
            _ => $"Unknown skill '{skillId}'. Available skills: engagement-summary, top-posts, recent-mentions, follower-insights, platform-comparison, check-notifications, provider-status"
        };
    }

    // `provider` is the resolved filter from ProviderSelection: null for every provider, or one
    // canonical provider id. Handlers never read the raw p.ProviderId.

    private static async Task<string> HandleEngagementSummaryAsync(IServiceProvider sp, string? provider, SkillParameters p, CancellationToken ct)
    {
        var analytics = sp.GetRequiredService<IAnalyticsService>();
        var summary = await analytics.GetEngagementSummaryAsync(provider, p.Since, ct);
        return FormatJson(summary);
    }

    private static async Task<string> HandleTopPostsAsync(IServiceProvider sp, string? provider, SkillParameters p, CancellationToken ct)
    {
        var analytics = sp.GetRequiredService<IAnalyticsService>();
        var posts = await analytics.GetTopPostsAsync(p.Count ?? 10, provider, p.Since, ct);
        return FormatJson(posts);
    }

    private static async Task<string> HandleRecentMentionsAsync(IServiceProvider sp, string? provider, SkillParameters p, CancellationToken ct)
    {
        var analytics = sp.GetRequiredService<IAnalyticsService>();
        var mentions = await analytics.GetRecentMentionsAsync(p.Count ?? 20, provider, ct);
        return FormatJson(mentions);
    }

    private static async Task<string> HandleFollowerInsightsAsync(IServiceProvider sp, string? provider, SkillParameters p, CancellationToken ct)
    {
        var analytics = sp.GetRequiredService<IAnalyticsService>();
        var engagers = await analytics.GetTopEngagersAsync(p.Count ?? 10, provider, p.Since, ct);
        return FormatJson(engagers);
    }

    private static async Task<string> HandlePlatformComparisonAsync(IServiceProvider sp, SkillParameters p, CancellationToken ct)
    {
        var analytics = sp.GetRequiredService<IAnalyticsService>();
        var comparison = await analytics.GetPlatformComparisonAsync(p.Since, ct);
        return FormatJson(comparison);
    }

    private static async Task<string> HandleCheckNotificationsAsync(IServiceProvider sp, string? provider, CancellationToken ct)
    {
        var repository = sp.GetRequiredService<ISocialDataRepository>();
        var unread = await repository.GetUnreadNotificationsAsync(provider, ct);
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

    // Compact summary of incoming metadata for logging — recognized keys only, with skill/skillId
    // dropped because they are already logged separately. Returns "{}" when nothing recognized.
    private static string SummarizeParameters(IReadOnlyDictionary<string, JsonElement>? parameters)
    {
        if (parameters is null || parameters.Count == 0) return "{}";
        var recognized = new[] { "providerId", "count", "since" };
        var parts = new List<string>();
        foreach (var key in recognized)
        {
            if (parameters.TryGetValue(key, out var element) && element.ValueKind != JsonValueKind.Null)
            {
                parts.Add($"{key}={element.ToString()}");
            }
        }
        return parts.Count == 0 ? "{}" : "{" + string.Join(", ", parts) + "}";
    }

    private readonly struct SkillParameters
    {
        private readonly IReadOnlyDictionary<string, JsonElement>? _values;
        public SkillParameters(IReadOnlyDictionary<string, JsonElement>? values) => _values = values;

        public string? ProviderId => GetString("providerId");
        public int? Count => GetInt("count");
        public DateTimeOffset? Since => GetDateTimeOffset("since");

        private string? GetString(string key)
        {
            if (_values is null || !_values.TryGetValue(key, out var element)) return null;
            return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        }

        private int? GetInt(string key)
        {
            if (_values is null || !_values.TryGetValue(key, out var element)) return null;
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var n)) return n;
            if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
            return null;
        }

        private DateTimeOffset? GetDateTimeOffset(string key)
        {
            if (_values is null || !_values.TryGetValue(key, out var element)) return null;
            if (element.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)) return dto;
            return null;
        }
    }
}
