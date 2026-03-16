using SocialAgent.Core.Models;

namespace SocialAgent.Data.Repositories;

public interface ISocialDataRepository
{
    // Posts
    Task UpsertPostsAsync(IEnumerable<SocialPost> posts, CancellationToken ct = default);
    Task<IReadOnlyList<SocialPost>> GetPostsAsync(string? providerId = null, DateTimeOffset? since = null, bool? isOwnPost = null, int? limit = null, CancellationToken ct = default);
    Task<IReadOnlyList<SocialPost>> GetTopPostsByEngagementAsync(int count, string? providerId = null, DateTimeOffset? since = null, CancellationToken ct = default);

    // Notifications
    Task UpsertNotificationsAsync(IEnumerable<SocialNotification> notifications, CancellationToken ct = default);
    Task<IReadOnlyList<SocialNotification>> GetNotificationsAsync(string? providerId = null, string? type = null, DateTimeOffset? since = null, int? limit = null, CancellationToken ct = default);
    Task<IReadOnlyList<SocialNotification>> GetUnreadNotificationsAsync(string? providerId = null, CancellationToken ct = default);

    // Profiles
    Task UpsertProfileAsync(SocialProfile profile, CancellationToken ct = default);
    Task<IReadOnlyList<SocialProfile>> GetProfilesAsync(CancellationToken ct = default);

    // Poll State
    Task<PollState?> GetPollStateAsync(string providerId, CancellationToken ct = default);
    Task UpsertPollStateAsync(PollState state, CancellationToken ct = default);
}
