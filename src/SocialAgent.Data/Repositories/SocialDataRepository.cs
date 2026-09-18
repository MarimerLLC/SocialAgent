using Microsoft.EntityFrameworkCore;
using SocialAgent.Core.Models;

namespace SocialAgent.Data.Repositories;

public class SocialDataRepository(SocialAgentDbContext db) : ISocialDataRepository
{
    public async Task UpsertPostsAsync(IEnumerable<SocialPost> posts, CancellationToken ct = default)
    {
        var incoming = posts.ToList();
        if (incoming.Count == 0)
        {
            return;
        }

        // One lookup for the whole batch instead of a SELECT per post.
        var providerIds = incoming.Select(p => p.ProviderId).Distinct().ToList();
        var platformIds = incoming.Select(p => p.PlatformPostId).Distinct().ToList();
        var existing = await db.Posts
            .Where(p => providerIds.Contains(p.ProviderId) && platformIds.Contains(p.PlatformPostId))
            .ToDictionaryAsync(p => (p.ProviderId, p.PlatformPostId), ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var post in incoming)
        {
            if (existing.TryGetValue((post.ProviderId, post.PlatformPostId), out var match))
            {
                match.LikeCount = post.LikeCount;
                match.RepostCount = post.RepostCount;
                match.ReplyCount = post.ReplyCount;
                match.LastUpdated = now;
            }
            else
            {
                db.Posts.Add(post);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SocialPost>> GetPostsAsync(
        string? providerId = null, DateTimeOffset? since = null,
        bool? isOwnPost = null, int? limit = null, CancellationToken ct = default)
    {
        var query = db.Posts.AsQueryable();
        if (providerId is not null) query = query.Where(p => p.ProviderId == providerId);
        if (since is not null) query = query.Where(p => p.CreatedAt >= since);
        if (isOwnPost is not null) query = query.Where(p => p.IsOwnPost == isOwnPost);
        query = query.OrderByDescending(p => p.CreatedAt);
        if (limit is not null) query = query.Take(limit.Value);
        return await query.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SocialPost>> GetTopPostsByEngagementAsync(
        int count, string? providerId = null, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var query = db.Posts.Where(p => p.IsOwnPost);
        if (providerId is not null) query = query.Where(p => p.ProviderId == providerId);
        if (since is not null) query = query.Where(p => p.CreatedAt >= since);
        return await query
            .OrderByDescending(p => p.LikeCount + p.RepostCount + p.ReplyCount)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task UpsertNotificationsAsync(IEnumerable<SocialNotification> notifications, CancellationToken ct = default)
    {
        var incoming = notifications.ToList();
        if (incoming.Count == 0)
        {
            return;
        }

        // One lookup for the whole batch instead of a SELECT per notification.
        var providerIds = incoming.Select(n => n.ProviderId).Distinct().ToList();
        var platformIds = incoming.Select(n => n.PlatformNotificationId).Distinct().ToList();
        var existing = await db.Notifications
            .Where(n => providerIds.Contains(n.ProviderId) && platformIds.Contains(n.PlatformNotificationId))
            .ToDictionaryAsync(n => (n.ProviderId, n.PlatformNotificationId), ct);

        foreach (var notification in incoming)
        {
            if (existing.TryGetValue((notification.ProviderId, notification.PlatformNotificationId), out var match))
            {
                match.IsRead = notification.IsRead;
            }
            else
            {
                db.Notifications.Add(notification);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SocialNotification>> GetNotificationsAsync(
        string? providerId = null, string? type = null,
        DateTimeOffset? since = null, int? limit = null, CancellationToken ct = default)
    {
        var query = db.Notifications.AsQueryable();
        if (providerId is not null) query = query.Where(n => n.ProviderId == providerId);
        if (type is not null) query = query.Where(n => n.Type == type);
        if (since is not null) query = query.Where(n => n.CreatedAt >= since);
        query = query.OrderByDescending(n => n.CreatedAt);
        if (limit is not null) query = query.Take(limit.Value);
        return await query.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SocialNotification>> GetUnreadNotificationsAsync(
        string? providerId = null, CancellationToken ct = default)
    {
        var query = db.Notifications.Where(n => !n.IsRead);
        if (providerId is not null) query = query.Where(n => n.ProviderId == providerId);
        return await query.OrderByDescending(n => n.CreatedAt).ToListAsync(ct);
    }

    public async Task<PostEngagementTotals> GetPostEngagementTotalsAsync(
        string? providerId = null, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var query = db.Posts.Where(p => p.IsOwnPost);
        if (providerId is not null) query = query.Where(p => p.ProviderId == providerId);
        if (since is not null) query = query.Where(p => p.CreatedAt >= since);

        // Aggregated in SQL; the whole period never lands in memory.
        var totals = await query
            .GroupBy(_ => 1)
            .Select(g => new PostEngagementTotals(
                g.Count(),
                g.Sum(p => p.LikeCount),
                g.Sum(p => p.RepostCount),
                g.Sum(p => p.ReplyCount)))
            .FirstOrDefaultAsync(ct);

        return totals;
    }

    public async Task<IReadOnlyDictionary<string, int>> GetNotificationCountsByTypeAsync(
        string? providerId = null, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var query = db.Notifications.AsQueryable();
        if (providerId is not null) query = query.Where(n => n.ProviderId == providerId);
        if (since is not null) query = query.Where(n => n.CreatedAt >= since);

        return await query
            .GroupBy(n => n.Type)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Type, x => x.Count, ct);
    }

    public async Task<IReadOnlyList<EngagerTally>> GetEngagerTalliesAsync(
        string? providerId = null, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var query = db.Notifications.AsQueryable();
        if (providerId is not null) query = query.Where(n => n.ProviderId == providerId);
        if (since is not null) query = query.Where(n => n.CreatedAt >= since);

        return await query
            .GroupBy(n => new { n.FromHandle, n.Type })
            .Select(g => new EngagerTally(g.Key.FromHandle, g.Key.Type, g.Count()))
            .ToListAsync(ct);
    }

    public async Task UpsertProfileAsync(SocialProfile profile, CancellationToken ct = default)
    {
        var existing = await db.Profiles.FindAsync([profile.ProviderId], ct);
        if (existing is null)
        {
            db.Profiles.Add(profile);
        }
        else
        {
            existing.Handle = profile.Handle;
            existing.DisplayName = profile.DisplayName;
            existing.Bio = profile.Bio;
            existing.AvatarUrl = profile.AvatarUrl;
            existing.FollowerCount = profile.FollowerCount;
            existing.FollowingCount = profile.FollowingCount;
            existing.PostCount = profile.PostCount;
            existing.LastUpdated = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SocialProfile>> GetProfilesAsync(CancellationToken ct = default)
    {
        return await db.Profiles.ToListAsync(ct);
    }

    public async Task<PollState?> GetPollStateAsync(string providerId, CancellationToken ct = default)
    {
        return await db.PollStates.FindAsync([providerId], ct);
    }

    public async Task UpsertPollStateAsync(PollState state, CancellationToken ct = default)
    {
        var existing = await db.PollStates.FindAsync([state.ProviderId], ct);
        if (existing is null)
        {
            db.PollStates.Add(state);
        }
        else
        {
            existing.LastPostId = state.LastPostId;
            existing.LastNotificationId = state.LastNotificationId;
            existing.LastPollTime = state.LastPollTime;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<ProviderToken?> GetProviderTokenAsync(string providerId, CancellationToken ct = default)
    {
        return await db.ProviderTokens.FindAsync([providerId], ct);
    }

    public async Task UpsertProviderTokenAsync(ProviderToken token, CancellationToken ct = default)
    {
        var existing = await db.ProviderTokens.FindAsync([token.ProviderId], ct);
        if (existing is null)
        {
            db.ProviderTokens.Add(token);
        }
        else
        {
            existing.AccessToken = token.AccessToken;
            existing.ExpiresAt = token.ExpiresAt;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> PurgeOldPostsAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        return await db.Posts
            .Where(p => p.CreatedAt < olderThan)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<int> PurgeOldNotificationsAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        return await db.Notifications
            .Where(n => n.CreatedAt < olderThan)
            .ExecuteDeleteAsync(ct);
    }
}
