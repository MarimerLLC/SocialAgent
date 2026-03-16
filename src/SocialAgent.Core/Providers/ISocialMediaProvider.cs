namespace SocialAgent.Core.Providers;

using SocialAgent.Core.Models;

public interface ISocialMediaProvider
{
    string ProviderId { get; }
    string ProviderName { get; }

    Task<bool> ValidateConnectionAsync(CancellationToken ct = default);
    Task<SocialProfile> GetProfileAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SocialPost>> GetRecentPostsAsync(DateTimeOffset? since = null, CancellationToken ct = default);
    Task<IReadOnlyList<SocialNotification>> GetNotificationsAsync(DateTimeOffset? since = null, CancellationToken ct = default);
}
