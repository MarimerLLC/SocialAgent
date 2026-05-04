using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocialAgent.Core.Models;
using SocialAgent.Core.Providers;

namespace SocialAgent.Providers.Threads;

public class ThreadsProvider(
    HttpClient httpClient,
    IOptions<ThreadsOptions> options,
    ThreadsTokenStore tokenStore,
    ILogger<ThreadsProvider> logger) : ISocialMediaProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ThreadsOptions _options = options.Value;

    public string ProviderId => "threads";
    public string ProviderName => "Threads";

    public async Task<bool> ValidateConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var user = await GetAsync<ThreadsUser>("/v1.0/me?fields=id", ct);
            return user is not null && !string.IsNullOrEmpty(user.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Threads connection validation failed");
            return false;
        }
    }

    public async Task<SocialProfile> GetProfileAsync(CancellationToken ct = default)
    {
        var user = await GetAsync<ThreadsUser>(
            "/v1.0/me?fields=id,username,name,threads_profile_picture_url,threads_biography", ct)
            ?? throw new InvalidOperationException("Failed to get Threads user");

        var followerCount = 0;
        if (_options.IncludePostInsights && !string.IsNullOrEmpty(user.Id))
        {
            try
            {
                var insights = await GetAsync<ThreadsInsightsResponse>(
                    $"/v1.0/{user.Id}/insights?metric=followers_count", ct);
                followerCount = insights?.Data?.FirstOrDefault()?.TotalValue?.Value
                    ?? insights?.Data?.FirstOrDefault()?.Values?.FirstOrDefault()?.Value
                    ?? 0;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch Threads follower count (insights scope may be missing)");
            }
        }

        return new SocialProfile
        {
            ProviderId = ProviderId,
            Handle = user.Username ?? string.Empty,
            DisplayName = user.Name,
            Bio = user.ThreadsBiography,
            AvatarUrl = user.ThreadsProfilePictureUrl,
            FollowerCount = followerCount,
            FollowingCount = 0,
            PostCount = 0
        };
    }

    public async Task<IReadOnlyList<SocialPost>> GetRecentPostsAsync(DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var url = "/v1.0/me/threads?fields=id,text,timestamp,permalink,replies_count,reposts_count,quotes_count,media_type,media_url,is_quote_post,username&limit=25";
        if (since is not null)
        {
            url += $"&since={Uri.EscapeDataString(since.Value.ToUniversalTime().ToString("o"))}";
        }

        var response = await GetAsync<ThreadsListResponse<ThreadsConversationItem>>(url, ct);
        var items = response?.Data ?? [];

        var posts = new List<SocialPost>(items.Count);
        foreach (var item in items)
        {
            var likeCount = 0;
            if (_options.IncludePostInsights)
            {
                try
                {
                    var insights = await GetAsync<ThreadsInsightsResponse>(
                        $"/v1.0/{item.Id}/insights?metric=likes", ct);
                    likeCount = insights?.Data?.FirstOrDefault()?.Values?.FirstOrDefault()?.Value
                        ?? insights?.Data?.FirstOrDefault()?.TotalValue?.Value
                        ?? 0;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to fetch Threads insights for thread {Id}", item.Id);
                }
            }
            posts.Add(MapToSocialPost(item, isOwn: true, likeCount));
        }
        return posts;
    }

    public async Task<IReadOnlyList<SocialNotification>> GetNotificationsAsync(DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var mentions = await SafeGetListAsync(BuildNotificationUrl("/v1.0/me/mentions", since), ct);
        var replies = await SafeGetListAsync(BuildNotificationUrl("/v1.0/me/replies", since), ct);

        var seen = new HashSet<string>();
        var notifications = new List<SocialNotification>(mentions.Count + replies.Count);

        foreach (var item in mentions)
        {
            if (!seen.Add($"mention:{item.Id}")) continue;
            notifications.Add(MapToSocialNotification(item, "mention"));
        }
        foreach (var item in replies)
        {
            if (!seen.Add($"reply:{item.Id}")) continue;
            notifications.Add(MapToSocialNotification(item, "reply"));
        }

        return notifications;
    }

    public async Task<(string Token, DateTimeOffset ExpiresAt)?> RefreshTokenAsync(CancellationToken ct = default)
    {
        var (currentToken, _) = tokenStore.Current;
        var url = $"/refresh_access_token?grant_type=th_refresh_token&access_token={Uri.EscapeDataString(currentToken)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var refreshed = await response.Content.ReadFromJsonAsync<ThreadsRefreshResponse>(JsonOptions, ct);
            if (refreshed is null || string.IsNullOrEmpty(refreshed.AccessToken))
            {
                logger.LogWarning("Threads token refresh returned an empty response");
                return null;
            }
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn);
            tokenStore.Set(refreshed.AccessToken, expiresAt);
            logger.LogInformation("Threads access token refreshed; new expiry {Expiry:o}", expiresAt);
            return (refreshed.AccessToken, expiresAt);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to refresh Threads access token");
            return null;
        }
    }

    private static string BuildNotificationUrl(string path, DateTimeOffset? since)
    {
        var url = $"{path}?fields=id,text,timestamp,permalink,username,replies_count,reposts_count,quotes_count&limit=25";
        if (since is not null)
        {
            url += $"&since={Uri.EscapeDataString(since.Value.ToUniversalTime().ToString("o"))}";
        }
        return url;
    }

    private async Task<IReadOnlyList<ThreadsConversationItem>> SafeGetListAsync(string url, CancellationToken ct)
    {
        try
        {
            var response = await GetAsync<ThreadsListResponse<ThreadsConversationItem>>(url, ct);
            return response?.Data ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch Threads list at {Url} (scope may be missing)", url);
            return [];
        }
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        var (token, _) = tokenStore.Current;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    private SocialPost MapToSocialPost(ThreadsConversationItem item, bool isOwn, int likeCount)
    {
        return new SocialPost
        {
            Id = $"threads:{item.Id}",
            ProviderId = ProviderId,
            PlatformPostId = item.Id,
            AuthorHandle = item.Username ?? "unknown",
            Content = item.Text ?? string.Empty,
            CreatedAt = item.Timestamp,
            Url = item.Permalink,
            LikeCount = likeCount,
            RepostCount = item.RepostsCount + item.QuotesCount,
            ReplyCount = item.RepliesCount,
            IsOwnPost = isOwn
        };
    }

    private SocialNotification MapToSocialNotification(ThreadsConversationItem item, string type)
    {
        return new SocialNotification
        {
            Id = $"threads:{type}:{item.Id}",
            ProviderId = ProviderId,
            PlatformNotificationId = $"{type}:{item.Id}",
            Type = type,
            FromHandle = item.Username ?? "unknown",
            CreatedAt = item.Timestamp,
            Content = item.Text,
            // Threads has no native read marker. Notifications stay unread until a higher-layer
            // mark-as-read mechanism is added.
            IsRead = false
        };
    }
}
