using SocialAgent.Core.Models;

namespace SocialAgent.Data.Repositories;

/// <summary>Aggregated post engagement for a period, computed in the database.</summary>
public readonly record struct PostEngagementTotals(int PostCount, int Likes, int Reposts, int Replies)
{
    public static PostEngagementTotals Empty => new(0, 0, 0, 0);
}

/// <summary>One (handle, interaction type) tally, computed in the database.</summary>
public readonly record struct EngagerTally(string Handle, string Type, int Count);

public interface ISocialDataRepository
{
    // Posts
    Task UpsertPostsAsync(IEnumerable<SocialPost> posts, CancellationToken ct = default);
    Task<IReadOnlyList<SocialPost>> GetPostsAsync(string? providerId = null, DateTimeOffset? since = null, bool? isOwnPost = null, int? limit = null, CancellationToken ct = default);
    Task<IReadOnlyList<SocialPost>> GetTopPostsByEngagementAsync(int count, string? providerId = null, DateTimeOffset? since = null, CancellationToken ct = default);

    /// <summary>Sums engagement across own posts without materialising them.</summary>
    Task<PostEngagementTotals> GetPostEngagementTotalsAsync(string? providerId = null, DateTimeOffset? since = null, CancellationToken ct = default);

    // Notifications
    Task UpsertNotificationsAsync(IEnumerable<SocialNotification> notifications, CancellationToken ct = default);
    Task<IReadOnlyList<SocialNotification>> GetNotificationsAsync(string? providerId = null, string? type = null, DateTimeOffset? since = null, int? limit = null, CancellationToken ct = default);
    Task<IReadOnlyList<SocialNotification>> GetUnreadNotificationsAsync(string? providerId = null, CancellationToken ct = default);

    /// <summary>Counts notifications per type without materialising them.</summary>
    Task<IReadOnlyDictionary<string, int>> GetNotificationCountsByTypeAsync(string? providerId = null, DateTimeOffset? since = null, CancellationToken ct = default);

    /// <summary>
    /// Tallies interactions per (handle, type). The result is bounded by the number of distinct
    /// engagers rather than the number of notifications.
    /// </summary>
    Task<IReadOnlyList<EngagerTally>> GetEngagerTalliesAsync(string? providerId = null, DateTimeOffset? since = null, CancellationToken ct = default);

    // Profiles
    Task UpsertProfileAsync(SocialProfile profile, CancellationToken ct = default);
    Task<IReadOnlyList<SocialProfile>> GetProfilesAsync(CancellationToken ct = default);

    // Poll State
    Task<PollState?> GetPollStateAsync(string providerId, CancellationToken ct = default);
    Task UpsertPollStateAsync(PollState state, CancellationToken ct = default);

    // Provider Tokens (for refreshable OAuth bearer tokens)
    Task<ProviderToken?> GetProviderTokenAsync(string providerId, CancellationToken ct = default);
    Task UpsertProviderTokenAsync(ProviderToken token, CancellationToken ct = default);

    // Retention
    Task<int> PurgeOldPostsAsync(DateTimeOffset olderThan, CancellationToken ct = default);
    Task<int> PurgeOldNotificationsAsync(DateTimeOffset olderThan, CancellationToken ct = default);
}
