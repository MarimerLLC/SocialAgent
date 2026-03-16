using SocialAgent.Core.Analytics;
using SocialAgent.Core.Models;
using SocialAgent.Data.Repositories;

namespace SocialAgent.Analytics;

public class AnalyticsService(ISocialDataRepository repository) : IAnalyticsService
{
    public async Task<EngagementSummary> GetEngagementSummaryAsync(
        string? providerId = null, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var effectiveSince = since ?? DateTimeOffset.UtcNow.AddDays(-7);
        var posts = await repository.GetPostsAsync(providerId, effectiveSince, isOwnPost: true, ct: ct);
        var notifications = await repository.GetNotificationsAsync(providerId, since: effectiveSince, ct: ct);

        var totalLikes = posts.Sum(p => p.LikeCount);
        var totalReposts = posts.Sum(p => p.RepostCount);
        var totalReplies = posts.Sum(p => p.ReplyCount);
        var postCount = posts.Count;

        var topEngagers = notifications
            .GroupBy(n => n.FromHandle)
            .Select(g => new TopEngager
            {
                Handle = g.Key,
                InteractionCount = g.Count(),
                MostCommonInteractionType = g.GroupBy(n => n.Type)
                    .OrderByDescending(tg => tg.Count())
                    .First().Key
            })
            .OrderByDescending(e => e.InteractionCount)
            .Take(10)
            .ToList();

        return new EngagementSummary
        {
            ProviderId = providerId ?? "all",
            PeriodStart = effectiveSince,
            PeriodEnd = DateTimeOffset.UtcNow,
            TotalPosts = postCount,
            TotalLikes = totalLikes,
            TotalReposts = totalReposts,
            TotalReplies = totalReplies,
            TotalMentions = notifications.Count(n => n.Type == "mention"),
            NewFollowers = notifications.Count(n => n.Type == "follow"),
            AvgLikesPerPost = postCount > 0 ? (double)totalLikes / postCount : 0,
            AvgRepostsPerPost = postCount > 0 ? (double)totalReposts / postCount : 0,
            AvgRepliesPerPost = postCount > 0 ? (double)totalReplies / postCount : 0,
            TopEngagers = topEngagers
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
        var notifications = await repository.GetNotificationsAsync(providerId, since: effectiveSince, ct: ct);

        return notifications
            .GroupBy(n => n.FromHandle)
            .Select(g => new TopEngager
            {
                Handle = g.Key,
                InteractionCount = g.Count(),
                MostCommonInteractionType = g.GroupBy(n => n.Type)
                    .OrderByDescending(tg => tg.Count())
                    .First().Key
            })
            .OrderByDescending(e => e.InteractionCount)
            .Take(count)
            .ToList();
    }

    public async Task<IReadOnlyList<EngagementSummary>> GetPlatformComparisonAsync(
        DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var profiles = await repository.GetProfilesAsync(ct);
        var summaries = new List<EngagementSummary>();
        foreach (var profile in profiles)
        {
            var summary = await GetEngagementSummaryAsync(profile.ProviderId, since, ct);
            summaries.Add(summary);
        }
        return summaries;
    }
}
