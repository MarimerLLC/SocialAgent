using System.Net;
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
    IHttpClientFactory httpClientFactory,
    IOptions<BlueskyOptions> options,
    ILogger<BlueskyProvider> logger) : ISocialMediaProvider
{
    /// <summary>Named <see cref="HttpClient"/> registered by <c>AddBlueskyProvider</c>.</summary>
    public const string HttpClientName = "bluesky";

    private const int PageSize = 50;

    // Bounds a single poll while still guaranteeing the cursor loop terminates.
    private const int MaxPages = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly BlueskyOptions _options = options.Value;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private BlueskySession? _session;

    public string ProviderId => "bluesky";
    public string ProviderName => "Bluesky";

    public async Task<bool> ValidateConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var session = await EnsureSessionAsync(ct);
            // Exercise an authenticated call rather than trusting a possibly-expired cached session.
            _ = await GetAsync<BlueskyProfile>(
                $"/xrpc/app.bsky.actor.getProfile?actor={Uri.EscapeDataString(session.Did)}", ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Bluesky connection validation failed");
            return false;
        }
    }

    public async Task<SocialProfile> GetProfileAsync(CancellationToken ct = default)
    {
        var session = await EnsureSessionAsync(ct);
        var profile = await GetAsync<BlueskyProfile>(
            $"/xrpc/app.bsky.actor.getProfile?actor={Uri.EscapeDataString(session.Did)}", ct)
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
        var session = await EnsureSessionAsync(ct);
        var posts = new List<SocialPost>();
        string? cursor = null;

        for (var page = 0; page < MaxPages; page++)
        {
            var url = $"/xrpc/app.bsky.feed.getAuthorFeed?actor={Uri.EscapeDataString(session.Did)}&limit={PageSize}";
            if (cursor is not null)
            {
                url += $"&cursor={Uri.EscapeDataString(cursor)}";
            }

            var response = await GetAsync<BlueskyFeedResponse>(url, ct);
            if (response is null || response.Feed.Count == 0)
            {
                break;
            }

            var reachedCutoff = false;
            foreach (var item in response.Feed)
            {
                // The feed is ordered by indexedAt, so that is what the cutoff has to test against
                // even though the mapped CreatedAt prefers the record's authoring timestamp.
                if (since is not null && item.Post.IndexedAt < since)
                {
                    reachedCutoff = true;
                    continue;
                }
                posts.Add(MapToSocialPost(item.Post, isOwn: true));
            }

            cursor = response.Cursor;
            // No `since` means this is a first-ever poll; one page is backfill enough.
            if (since is null || reachedCutoff || cursor is null || response.Feed.Count < PageSize)
            {
                break;
            }
        }

        return posts;
    }

    public async Task<IReadOnlyList<SocialNotification>> GetNotificationsAsync(DateTimeOffset? since = null, CancellationToken ct = default)
    {
        await EnsureSessionAsync(ct);
        var notifications = new List<SocialNotification>();
        string? cursor = null;

        for (var page = 0; page < MaxPages; page++)
        {
            var url = $"/xrpc/app.bsky.notification.listNotifications?limit={PageSize}";
            if (cursor is not null)
            {
                url += $"&cursor={Uri.EscapeDataString(cursor)}";
            }

            var response = await GetAsync<BlueskyNotificationResponse>(url, ct);
            if (response is null || response.Notifications.Count == 0)
            {
                break;
            }

            var reachedCutoff = false;
            foreach (var notification in response.Notifications)
            {
                if (since is not null && notification.IndexedAt < since)
                {
                    reachedCutoff = true;
                    continue;
                }
                notifications.Add(MapToSocialNotification(notification));
            }

            cursor = response.Cursor;
            if (since is null || reachedCutoff || cursor is null || response.Notifications.Count < PageSize)
            {
                break;
            }
        }

        return notifications;
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await SendAuthenticatedAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    /// <summary>
    /// Sends an authenticated request, recovering from an expired session in-process. Bluesky
    /// access JWTs live about two hours, so without this the provider would 401 on every call
    /// until the pod restarted.
    /// </summary>
    private async Task<HttpResponseMessage> SendAuthenticatedAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        var session = await EnsureSessionAsync(ct);
        var response = await SendAsync(requestFactory, session.AccessJwt, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        session = await RenewSessionAsync(session, ct);
        return await SendAsync(requestFactory, session.AccessJwt, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory, string accessJwt, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = requestFactory();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessJwt);
        return await client.SendAsync(request, ct);
    }

    private async Task<BlueskySession> EnsureSessionAsync(CancellationToken ct)
    {
        var existing = Volatile.Read(ref _session);
        if (existing is not null)
        {
            return existing;
        }

        await _sessionLock.WaitAsync(ct);
        try
        {
            return Volatile.Read(ref _session) ?? await CreateSessionAsync(ct);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Trades the refresh JWT for a new session, falling back to a full login. <paramref name="stale"/>
    /// is the session that just produced a 401 — if a concurrent caller already replaced it, that
    /// caller's session is reused instead of issuing a second refresh.
    /// </summary>
    private async Task<BlueskySession> RenewSessionAsync(BlueskySession stale, CancellationToken ct)
    {
        await _sessionLock.WaitAsync(ct);
        try
        {
            var current = Volatile.Read(ref _session);
            if (current is not null && !ReferenceEquals(current, stale))
            {
                return current;
            }

            if (!string.IsNullOrEmpty(stale.RefreshJwt))
            {
                try
                {
                    var client = httpClientFactory.CreateClient(HttpClientName);
                    using var request = new HttpRequestMessage(
                        HttpMethod.Post, "/xrpc/com.atproto.server.refreshSession");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stale.RefreshJwt);
                    using var response = await client.SendAsync(request, ct);

                    if (response.IsSuccessStatusCode)
                    {
                        var refreshed = await response.Content.ReadFromJsonAsync<BlueskySession>(JsonOptions, ct);
                        if (refreshed is not null && !string.IsNullOrEmpty(refreshed.AccessJwt))
                        {
                            Volatile.Write(ref _session, refreshed);
                            logger.LogInformation("Bluesky session refreshed");
                            return refreshed;
                        }
                    }

                    logger.LogWarning(
                        "Bluesky session refresh returned {Status}; falling back to a full login",
                        response.StatusCode);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Bluesky session refresh failed; falling back to a full login");
                }
            }

            return await CreateSessionAsync(ct);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    // Caller must hold _sessionLock.
    private async Task<BlueskySession> CreateSessionAsync(CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.PostAsJsonAsync("/xrpc/com.atproto.server.createSession",
            new { identifier = _options.Handle, password = _options.AppPassword }, ct);

        response.EnsureSuccessStatusCode();

        var session = await response.Content.ReadFromJsonAsync<BlueskySession>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Failed to create Bluesky session");

        Volatile.Write(ref _session, session);
        return session;
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
            // indexedAt is when the relay saw the post; the record carries when it was authored.
            CreatedAt = post.Record?.CreatedAt ?? post.IndexedAt,
            Url = BuildPostUrl(post),
            LikeCount = post.LikeCount,
            RepostCount = post.RepostCount,
            ReplyCount = post.ReplyCount,
            IsOwnPost = isOwn
        };
    }

    private static string BuildPostUrl(BlueskyPostView post)
    {
        var rkey = post.Uri[(post.Uri.LastIndexOf('/') + 1)..];
        return $"https://bsky.app/profile/{post.Author?.Handle ?? "unknown"}/post/{rkey}";
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
            CreatedAt = notification.Record?.CreatedAt ?? notification.IndexedAt,
            Content = notification.Record?.Text,
            IsRead = notification.IsRead
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
