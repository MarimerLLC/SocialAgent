using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocialAgent.Core.Models;
using SocialAgent.Core.Providers;

namespace SocialAgent.Providers.Mastodon;

public class MastodonProvider(
    HttpClient httpClient,
    IOptions<MastodonOptions> options,
    ILogger<MastodonProvider> logger) : ISocialMediaProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly MastodonOptions _options = options.Value;

    public string ProviderId => "mastodon";
    public string ProviderName => "Mastodon";

    public async Task<bool> ValidateConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            ConfigureClient();
            var response = await httpClient.GetAsync("/api/v1/accounts/verify_credentials", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Mastodon connection validation failed");
            return false;
        }
    }

    public async Task<SocialProfile> GetProfileAsync(CancellationToken ct = default)
    {
        ConfigureClient();
        var account = await httpClient.GetFromJsonAsync<MastodonAccount>(
            "/api/v1/accounts/verify_credentials", JsonOptions, ct)
            ?? throw new InvalidOperationException("Failed to get Mastodon account");

        return new SocialProfile
        {
            ProviderId = ProviderId,
            Handle = account.Acct,
            DisplayName = account.DisplayName,
            Bio = account.Note,
            AvatarUrl = account.Avatar,
            FollowerCount = account.FollowersCount,
            FollowingCount = account.FollowingCount,
            PostCount = account.StatusesCount
        };
    }

    public async Task<IReadOnlyList<SocialPost>> GetRecentPostsAsync(DateTimeOffset? since = null, CancellationToken ct = default)
    {
        ConfigureClient();
        var account = await httpClient.GetFromJsonAsync<MastodonAccount>(
            "/api/v1/accounts/verify_credentials", JsonOptions, ct)
            ?? throw new InvalidOperationException("Failed to get Mastodon account");

        var url = $"/api/v1/accounts/{account.Id}/statuses?limit=40";
        var statuses = await httpClient.GetFromJsonAsync<List<MastodonStatus>>(url, JsonOptions, ct) ?? [];

        return statuses
            .Where(s => since is null || s.CreatedAt >= since)
            .Select(s => MapToSocialPost(s, account.Acct, isOwn: true))
            .ToList();
    }

    public async Task<IReadOnlyList<SocialNotification>> GetNotificationsAsync(DateTimeOffset? since = null, CancellationToken ct = default)
    {
        ConfigureClient();

        // Fetch the last-read marker to determine read state
        string? lastReadId = null;
        try
        {
            var markers = await httpClient.GetFromJsonAsync<MastodonMarkersResponse>(
                "/api/v1/markers?timeline[]=notifications", JsonOptions, ct);
            lastReadId = markers?.Notifications?.LastReadId;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch Mastodon notification markers, all will be marked unread");
        }

        var url = "/api/v1/notifications?limit=40";
        var notifications = await httpClient.GetFromJsonAsync<List<MastodonNotification>>(url, JsonOptions, ct) ?? [];

        return notifications
            .Where(n => since is null || n.CreatedAt >= since)
            .Select(n => MapToSocialNotification(n, lastReadId))
            .ToList();
    }

    private void ConfigureClient()
    {
        httpClient.BaseAddress ??= new Uri(_options.InstanceUrl);
        httpClient.DefaultRequestHeaders.Authorization ??=
            new AuthenticationHeaderValue("Bearer", _options.AccessToken);
    }

    private SocialPost MapToSocialPost(MastodonStatus status, string ownerAcct, bool isOwn)
    {
        return new SocialPost
        {
            Id = $"mastodon:{status.Id}",
            ProviderId = ProviderId,
            PlatformPostId = status.Id,
            AuthorHandle = status.Account?.Acct ?? ownerAcct,
            Content = status.Content,
            CreatedAt = status.CreatedAt,
            InReplyToId = status.InReplyToId,
            Url = status.Url,
            LikeCount = status.FavouritesCount,
            RepostCount = status.ReblogsCount,
            ReplyCount = status.RepliesCount,
            IsOwnPost = isOwn
        };
    }

    private SocialNotification MapToSocialNotification(MastodonNotification notification, string? lastReadId)
    {
        // Mastodon IDs are numeric strings — notification is read if its ID <= lastReadId
        var isRead = lastReadId is not null
            && long.TryParse(notification.Id, out var notifId)
            && long.TryParse(lastReadId, out var readId)
            && notifId <= readId;

        return new SocialNotification
        {
            Id = $"mastodon:{notification.Id}",
            ProviderId = ProviderId,
            PlatformNotificationId = notification.Id,
            Type = MapNotificationType(notification.Type),
            FromHandle = notification.Account?.Acct ?? "unknown",
            CreatedAt = notification.CreatedAt,
            RelatedPostId = notification.Status?.Id is not null ? $"mastodon:{notification.Status.Id}" : null,
            Content = notification.Status?.Content,
            IsRead = isRead
        };
    }

    private static string MapNotificationType(string mastodonType) => mastodonType switch
    {
        "mention" => "mention",
        "favourite" => "like",
        "reblog" => "repost",
        "follow" => "follow",
        "poll" => "poll",
        "status" => "status",
        _ => mastodonType
    };
}
