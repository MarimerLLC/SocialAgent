using SocialAgent.Core.Models;

namespace SocialAgent.Data.Repositories;

/// <summary>Aggregated post engagement for a period, computed in the database.</summary>
public readonly record struct PostEngagementTotals(int PostCount, int Likes, int Reposts, int Replies)
{
    public static PostEngagementTotals Empty => new(0, 0, 0, 0);
}

/// <summary>One (handle, interaction type) tally, computed in the database.</summary>
public readonly record struct EngagerTally(string Handle, string Type, int Count);

/// <summary>What <see cref="ISocialDataRepository.UpsertPostsAsync"/> changed.</summary>
/// <param name="Inserted">How many posts were new.</param>
/// <param name="EngagementChanged">Posts already stored whose like, repost or reply count moved.</param>
public readonly record struct PostUpsertResult(int Inserted, IReadOnlyList<SocialPost> EngagementChanged)
{
    // A default value (as from a test double) reads as "nothing changed" rather than null.
    public IReadOnlyList<SocialPost> EngagementChanged { get => field ?? []; init; } = EngagementChanged;
}

/// <summary>What <see cref="ISocialDataRepository.UpsertNotificationsAsync"/> changed.</summary>
/// <param name="Inserted">The notifications that were new, as stored.</param>
/// <param name="ReadStateChanged">How many stored notifications changed between read and unread.</param>
public readonly record struct NotificationUpsertResult(IReadOnlyList<SocialNotification> Inserted, int ReadStateChanged)
{
    public IReadOnlyList<SocialNotification> Inserted { get => field ?? []; init; } = Inserted;

    public int InsertedCount => Inserted.Count;
}

public interface ISocialDataRepository
{
    // Posts
    /// <summary>Inserts new posts and refreshes engagement on existing ones.</summary>
    Task<PostUpsertResult> UpsertPostsAsync(IEnumerable<SocialPost> posts, CancellationToken ct = default);

    /// <summary>One stored post by its platform id, or null.</summary>
    Task<SocialPost?> GetPostAsync(string providerId, string platformPostId, CancellationToken ct = default);
    Task<IReadOnlyList<SocialPost>> GetPostsAsync(string? providerId = null, DateTimeOffset? since = null, bool? isOwnPost = null, int? limit = null, CancellationToken ct = default);
    Task<IReadOnlyList<SocialPost>> GetTopPostsByEngagementAsync(int count, string? providerId = null, DateTimeOffset? since = null, CancellationToken ct = default);

    /// <summary>Sums engagement across own posts without materialising them.</summary>
    Task<PostEngagementTotals> GetPostEngagementTotalsAsync(string? providerId = null, DateTimeOffset? since = null, CancellationToken ct = default);

    // Notifications
    /// <summary>
    /// Inserts new notifications, and refreshes the read state (and a missing <c>Url</c>) of stored ones.
    /// </summary>
    Task<NotificationUpsertResult> UpsertNotificationsAsync(IEnumerable<SocialNotification> notifications, CancellationToken ct = default);

    /// <summary>
    /// Notifications newest first. <paramref name="type"/> and <paramref name="types"/> both filter
    /// by type; give either.
    /// </summary>
    Task<IReadOnlyList<SocialNotification>> GetNotificationsAsync(string? providerId = null, string? type = null, DateTimeOffset? since = null, int? limit = null, IReadOnlyCollection<string>? types = null, CancellationToken ct = default);
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

    /// <summary>
    /// The distinct provider ids that have stored posts or notifications, including providers
    /// that are no longer enabled but whose history has not yet aged out.
    /// </summary>
    Task<IReadOnlyList<string>> GetStoredProviderIdsAsync(CancellationToken ct = default);

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
