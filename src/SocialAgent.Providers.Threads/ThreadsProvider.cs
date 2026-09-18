using System.Net;
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
    IHttpClientFactory httpClientFactory,
    IOptions<ThreadsOptions> options,
    ThreadsTokenStore tokenStore,
    ILogger<ThreadsProvider> logger) : ISocialMediaProvider
{
    /// <summary>Named <see cref="HttpClient"/> registered by <c>AddThreadsProvider</c>.</summary>
    public const string HttpClientName = "threads";

    private const int PageSize = 25;

    // Bounds a single poll while still guaranteeing the cursor loop terminates.
    private const int MaxPages = 10;

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
        catch (Exception ex) when (!ct.IsCancellationRequested)
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
            catch (Exception ex) when (!ct.IsCancellationRequested)
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
        const string fields = "id,text,timestamp,permalink,replies_count,reposts_count,quotes_count,media_type,media_url,is_quote_post,username";
        var items = await GetPagedAsync($"/v1.0/me/threads?fields={fields}", since, ct);

        var posts = new List<SocialPost>(items.Count);
        foreach (var item in items)
        {
            var likeCount = 0;
            if (_options.IncludePostInsights)
            {
                // One extra call per post. Off by default precisely because of that cost.
                try
                {
                    var insights = await GetAsync<ThreadsInsightsResponse>(
                        $"/v1.0/{item.Id}/insights?metric=likes", ct);
                    likeCount = insights?.Data?.FirstOrDefault()?.Values?.FirstOrDefault()?.Value
                        ?? insights?.Data?.FirstOrDefault()?.TotalValue?.Value
                        ?? 0;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
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
        const string fields = "id,text,timestamp,permalink,username,replies_count,reposts_count,quotes_count";
        var mentions = await SafeGetPagedAsync($"/v1.0/me/mentions?fields={fields}", since, ct);
        var replies = await SafeGetPagedAsync($"/v1.0/me/replies?fields={fields}", since, ct);

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

    /// <summary>
    /// Exchanges the current long-lived token for a fresh one and stores it.
    /// </summary>
    public async Task<(string Token, DateTimeOffset ExpiresAt)?> RefreshTokenAsync(CancellationToken ct = default)
    {
        var (currentToken, _) = tokenStore.Current;

        try
        {
            var refreshed = await RequestRefreshAsync(currentToken, ct);
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
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Failed to refresh Threads access token");
            return null;
        }
    }

    /// <summary>
    /// Refreshes via the Authorization header so the token never lands in a URL — request URIs
    /// reach OpenTelemetry spans and exception messages. Meta documents this endpoint with the
    /// token as a query parameter, so a rejected header form falls back to the documented shape
    /// rather than silently failing a credential we only touch once every couple of months.
    /// </summary>
    private async Task<ThreadsRefreshResponse?> RequestRefreshAsync(string currentToken, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);

        using (var headerRequest = new HttpRequestMessage(
            HttpMethod.Get, "/refresh_access_token?grant_type=th_refresh_token"))
        {
            headerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", currentToken);
            using var headerResponse = await client.SendAsync(headerRequest, ct);

            if (headerResponse.IsSuccessStatusCode)
            {
                return await headerResponse.Content.ReadFromJsonAsync<ThreadsRefreshResponse>(JsonOptions, ct);
            }

            if (headerResponse.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden))
            {
                headerResponse.EnsureSuccessStatusCode();
            }

            logger.LogWarning(
                "Threads token refresh via Authorization header returned {Status}; retrying with the query-parameter form",
                headerResponse.StatusCode);
        }

        var url = $"/refresh_access_token?grant_type=th_refresh_token&access_token={Uri.EscapeDataString(currentToken)}";
        using var fallbackRequest = new HttpRequestMessage(HttpMethod.Get, url);
        using var fallbackResponse = await client.SendAsync(fallbackRequest, ct);
        fallbackResponse.EnsureSuccessStatusCode();
        return await fallbackResponse.Content.ReadFromJsonAsync<ThreadsRefreshResponse>(JsonOptions, ct);
    }

    private async Task<IReadOnlyList<ThreadsConversationItem>> SafeGetPagedAsync(
        string baseUrl, DateTimeOffset? since, CancellationToken ct)
    {
        try
        {
            return await GetPagedAsync(baseUrl, since, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to fetch Threads list at {Path} (scope may be missing)", PathOf(baseUrl));
            return [];
        }
    }

    private async Task<IReadOnlyList<ThreadsConversationItem>> GetPagedAsync(
        string baseUrl, DateTimeOffset? since, CancellationToken ct)
    {
        var items = new List<ThreadsConversationItem>();
        var url = $"{baseUrl}&limit={PageSize}";
        if (since is not null)
        {
            url += $"&since={Uri.EscapeDataString(since.Value.ToUniversalTime().ToString("o"))}";
        }

        var firstPageUrl = url;
        string? after = null;

        for (var page = 0; page < MaxPages; page++)
        {
            var pageUrl = after is null ? firstPageUrl : $"{firstPageUrl}&after={Uri.EscapeDataString(after)}";
            var response = await GetAsync<ThreadsListResponse<ThreadsConversationItem>>(pageUrl, ct);

            var data = response?.Data;
            if (data is null || data.Count == 0)
            {
                break;
            }
            items.AddRange(data);

            after = response?.Paging?.Cursors?.After;
            if (after is null || data.Count < PageSize)
            {
                break;
            }
        }

        return items;
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        var (token, _) = tokenStore.Current;
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    // Logs the path without the query string, which carries `since` cursors and field lists.
    private static string PathOf(string url)
    {
        var queryStart = url.IndexOf('?');
        return queryStart < 0 ? url : url[..queryStart];
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
