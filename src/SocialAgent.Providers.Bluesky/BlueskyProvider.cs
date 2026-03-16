using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocialAgent.Core.Models;
using SocialAgent.Core.Providers;

namespace SocialAgent.Providers.Bluesky;

public class BlueskyProvider(
    HttpClient httpClient,
    IOptions<BlueskyOptions> options,
    ILogger<BlueskyProvider> logger) : ISocialMediaProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly BlueskyOptions _options = options.Value;
    private string? _accessToken;
    private string? _did;

    public string ProviderId => "bluesky";
    public string ProviderName => "Bluesky";

    public async Task<bool> ValidateConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureAuthenticatedAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Bluesky connection validation failed");
            return false;
        }
    }

    public async Task<SocialProfile> GetProfileAsync(CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        var profile = await httpClient.GetFromJsonAsync<BlueskyProfile>(
            $"/xrpc/app.bsky.actor.getProfile?actor={_did}", JsonOptions, ct)
            ?? throw new InvalidOperationException("Failed to get Bluesky profile");

        return new SocialProfile
        {
            ProviderId = ProviderId,
            Handle = profile.Handle,
            DisplayName = profile.DisplayName,
            Bio = profile.Description,
            AvatarUrl = profile.Avatar,
            FollowerCount = profile.FollowersCount,
            FollowingCount = profile.FollowsCount,
            PostCount = profile.PostsCount
        };
    }

    public async Task<IReadOnlyList<SocialPost>> GetRecentPostsAsync(DateTimeOffset? since = null, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        var response = await httpClient.GetFromJsonAsync<BlueskyFeedResponse>(
            $"/xrpc/app.bsky.feed.getAuthorFeed?actor={_did}&limit=50", JsonOptions, ct)
            ?? new BlueskyFeedResponse();

        return response.Feed
            .Where(item => since is null || item.Post.IndexedAt >= since)
            .Select(item => MapToSocialPost(item.Post, isOwn: true))
            .ToList();
    }

    public async Task<IReadOnlyList<SocialNotification>> GetNotificationsAsync(DateTimeOffset? since = null, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        var response = await httpClient.GetFromJsonAsync<BlueskyNotificationResponse>(
            "/xrpc/app.bsky.notification.listNotifications?limit=50", JsonOptions, ct)
            ?? new BlueskyNotificationResponse();

        return response.Notifications
            .Where(n => since is null || n.IndexedAt >= since)
            .Select(MapToSocialNotification)
            .ToList();
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken ct)
    {
        if (_accessToken is not null)
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _accessToken);
            return;
        }

        httpClient.BaseAddress ??= new Uri(_options.ServiceUrl);

        var sessionResponse = await httpClient.PostAsJsonAsync("/xrpc/com.atproto.server.createSession",
            new { identifier = _options.Handle, password = _options.AppPassword }, ct);

        sessionResponse.EnsureSuccessStatusCode();

        var session = await sessionResponse.Content.ReadFromJsonAsync<BlueskySession>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Failed to create Bluesky session");

        _accessToken = session.AccessJwt;
        _did = session.Did;
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    private SocialPost MapToSocialPost(BlueskyPostView post, bool isOwn)
    {
        return new SocialPost
        {
            Id = $"bluesky:{post.Cid}",
            ProviderId = ProviderId,
            PlatformPostId = post.Uri,
            AuthorHandle = post.Author?.Handle ?? "unknown",
            Content = post.Record?.Text ?? string.Empty,
            CreatedAt = post.IndexedAt,
            Url = $"https://bsky.app/profile/{post.Author?.Handle}/post/{post.Uri.Split('/').Last()}",
            LikeCount = post.LikeCount,
            RepostCount = post.RepostCount,
            ReplyCount = post.ReplyCount,
            IsOwnPost = isOwn
        };
    }

    private SocialNotification MapToSocialNotification(BlueskyNotificationItem notification)
    {
        return new SocialNotification
        {
            Id = $"bluesky:{notification.Cid}",
            ProviderId = ProviderId,
            PlatformNotificationId = notification.Uri,
            Type = MapNotificationType(notification.Reason),
            FromHandle = notification.Author?.Handle ?? "unknown",
            CreatedAt = notification.IndexedAt,
            Content = notification.Record?.Text
        };
    }

    private static string MapNotificationType(string reason) => reason switch
    {
        "like" => "like",
        "repost" => "repost",
        "follow" => "follow",
        "mention" => "mention",
        "reply" => "reply",
        "quote" => "repost",
        _ => reason
    };
}
