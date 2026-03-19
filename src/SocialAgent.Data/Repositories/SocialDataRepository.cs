using Microsoft.EntityFrameworkCore;
using SocialAgent.Core.Models;

namespace SocialAgent.Data.Repositories;

public class SocialDataRepository(SocialAgentDbContext db) : ISocialDataRepository
{
    public async Task UpsertPostsAsync(IEnumerable<SocialPost> posts, CancellationToken ct = default)
    {
        foreach (var post in posts)
        {
            var existing = await db.Posts
                .FirstOrDefaultAsync(p => p.ProviderId == post.ProviderId && p.PlatformPostId == post.PlatformPostId, ct);

            if (existing is null)
            {
                db.Posts.Add(post);
            }
            else
            {
                existing.LikeCount = post.LikeCount;
                existing.RepostCount = post.RepostCount;
                existing.ReplyCount = post.ReplyCount;
                existing.LastUpdated = DateTimeOffset.UtcNow;
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
        foreach (var notification in notifications)
        {
            var existing = await db.Notifications
                .FirstOrDefaultAsync(n => n.ProviderId == notification.ProviderId && n.PlatformNotificationId == notification.PlatformNotificationId, ct);

            if (existing is null)
            {
                db.Notifications.Add(notification);
            }
            else
            {
                existing.IsRead = notification.IsRead;
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
}
