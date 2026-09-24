using SocialAgent.Core.Analytics;
using SocialAgent.Core.Models;
using SocialAgent.Data.Repositories;

namespace SocialAgent.Analytics;

public class AnalyticsService(ISocialDataRepository repository) : IAnalyticsService
{
    private const int TopEngagerCount = 10;

    public async Task<EngagementSummary> GetEngagementSummaryAsync(
        string? providerId = null, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var effectiveSince = since ?? DateTimeOffset.UtcNow.AddDays(-7);

        // Aggregated in the database rather than by pulling the whole period into memory.
        var totals = await repository.GetPostEngagementTotalsAsync(providerId, effectiveSince, ct);
        var typeCounts = await repository.GetNotificationCountsByTypeAsync(providerId, effectiveSince, ct);
        var tallies = await repository.GetEngagerTalliesAsync(providerId, effectiveSince, ct);

        var postCount = totals.PostCount;

        return new EngagementSummary
        {
            ProviderId = providerId ?? "all",
            PeriodStart = effectiveSince,
            PeriodEnd = DateTimeOffset.UtcNow,
            TotalPosts = postCount,
            TotalLikes = totals.Likes,
            TotalReposts = totals.Reposts,
            TotalReplies = totals.Replies,
            TotalMentions = typeCounts.GetValueOrDefault("mention"),
            NewFollowers = typeCounts.GetValueOrDefault("follow"),
            AvgLikesPerPost = postCount > 0 ? (double)totals.Likes / postCount : 0,
            AvgRepostsPerPost = postCount > 0 ? (double)totals.Reposts / postCount : 0,
            AvgRepliesPerPost = postCount > 0 ? (double)totals.Replies / postCount : 0,
            TopEngagers = RankEngagers(tallies, TopEngagerCount)
        };
    }

    public async Task<IReadOnlyList<SocialPost>> GetTopPostsAsync(
        int count = 10, string? providerId = null, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var effectiveSince = since ?? DateTimeOffset.UtcNow.AddDays(-30);
        return await repository.GetTopPostsByEngagementAsync(count, providerId, effectiveSince, ct);
    }

    public async Task<IReadOnlyList<SocialNotification>> GetRecentMentionsAsync(
        int count = 20, string? providerId = null, CancellationToken ct = default)
    {
        return await repository.GetNotificationsAsync(providerId, type: "mention", limit: count, ct: ct);
    }

    public async Task<IReadOnlyList<TopEngager>> GetTopEngagersAsync(
        int count = 10, string? providerId = null, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var effectiveSince = since ?? DateTimeOffset.UtcNow.AddDays(-30);
        var tallies = await repository.GetEngagerTalliesAsync(providerId, effectiveSince, ct);
        return RankEngagers(tallies, count);
    }

    public async Task<IReadOnlyList<EngagementSummary>> GetPlatformComparisonAsync(
        DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var profiles = await repository.GetProfilesAsync(ct);
        var summaries = new List<EngagementSummary>(profiles.Count);
        foreach (var profile in profiles)
        {
            summaries.Add(await GetEngagementSummaryAsync(profile.ProviderId, since, ct));
        }
        return summaries;
    }

    /// <summary>
    /// Reduces per-(handle, type) tallies to ranked engagers. The input is already aggregated, so
    /// this runs over distinct engagers rather than raw notifications.
    /// </summary>
    private static List<TopEngager> RankEngagers(IReadOnlyList<EngagerTally> tallies, int count)
    {
        return [.. tallies
            .GroupBy(t => t.Handle)
            .Select(g => new TopEngager
            {
                Handle = g.Key,
                InteractionCount = g.Sum(t => t.Count),
                MostCommonInteractionType = g
                    .OrderByDescending(t => t.Count)
                    .ThenBy(t => t.Type, StringComparer.Ordinal)
                    .First().Type
            })
            .OrderByDescending(e => e.InteractionCount)
            .ThenBy(e => e.Handle, StringComparer.Ordinal)
            .Take(count)];
    }
}
