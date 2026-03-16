namespace SocialAgent.Core.Analytics;

using SocialAgent.Core.Models;

public interface IAnalyticsService
{
    Task<EngagementSummary> GetEngagementSummaryAsync(string? providerId = null, DateTimeOffset? since = null, CancellationToken ct = default);
    Task<IReadOnlyList<SocialPost>> GetTopPostsAsync(int count = 10, string? providerId = null, DateTimeOffset? since = null, CancellationToken ct = default);
    Task<IReadOnlyList<SocialNotification>> GetRecentMentionsAsync(int count = 20, string? providerId = null, CancellationToken ct = default);
    Task<IReadOnlyList<TopEngager>> GetTopEngagersAsync(int count = 10, string? providerId = null, DateTimeOffset? since = null, CancellationToken ct = default);
    Task<IReadOnlyList<EngagementSummary>> GetPlatformComparisonAsync(DateTimeOffset? since = null, CancellationToken ct = default);
}
