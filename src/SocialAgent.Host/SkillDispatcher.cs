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
            // Known ids: the enabled providers, plus any provider that still has stored rows. A
            // null filter returns every stored row, so it must only stand in for a list that names
            // all of these.
            var knownProviderIds = ProviderSelection.KnownProviderIds(
                scope.ServiceProvider.GetServices<ISocialMediaProvider>().Select(x => x.ProviderId),
                await scope.ServiceProvider.GetRequiredService<ISocialDataRepository>().GetStoredProviderIdsAsync(ct));

            var selection = p.TryGetProviderId(out var requested, out var shapeError)
                ? ProviderSelection.Resolve(requested, knownProviderIds)
                : ProviderSelectionResult.Invalid(shapeError!);
            if (!selection.IsValid)
            {
                logger.LogInformation("Rejected skill {SkillId} ({RoutedFrom}) parameters={Parameters}: {Error}",
                    skillId, routedFrom, parameterSummary, selection.Error);
                return FormatError(selection.Error!, knownProviderIds);
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
    // canonical provider id. Handlers never see the raw providerId.

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

    // A rejected request answers with a JSON object carrying an "error" key, so a caller can tell
    // it from skill output (always a JSON array or object of data) without parsing English. The
    // relaxed encoder keeps the message readable ('x' rather than 'x'); the reply is
    // an A2A text part, never HTML.
    private static readonly JsonSerializerOptions ErrorJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static string FormatError(string error, IReadOnlyList<string> validProviderIds) =>
        JsonSerializer.Serialize(new { error, validProviderIds }, ErrorJson);

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

        public int? Count => GetInt("count");
        public DateTimeOffset? Since => GetDateTimeOffset("since");

        /// <summary>
        /// Reads providerId. Unlike count and since, a value of the wrong shape is an error rather
        /// than ignored, because ignoring it widens the query to every provider. Absent or JSON
        /// null is no filter. A string is used as given, and an array of strings (an easy shape
        /// for an LLM caller) is joined into the comma-separated form.
        /// </summary>
        public bool TryGetProviderId(out string? value, out string? error)
        {
            value = null;
            error = null;
            if (_values is null || !_values.TryGetValue("providerId", out var element)
                || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return true;
            }

            if (element.ValueKind == JsonValueKind.String)
            {
                value = element.GetString();
                return true;
            }

            if (element.ValueKind == JsonValueKind.Array
                && element.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String))
            {
                value = string.Join(",", element.EnumerateArray().Select(e => e.GetString()));
                return true;
            }

            error = $"providerId must be a string, or an array of strings; got {element.GetRawText()}.";
            return false;
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
