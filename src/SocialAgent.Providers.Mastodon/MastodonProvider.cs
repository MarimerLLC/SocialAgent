using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocialAgent.Core.Models;
using SocialAgent.Core.Providers;
using SocialAgent.Core.Text;

namespace SocialAgent.Providers.Mastodon;

public class MastodonProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<MastodonOptions> options,
    ILogger<MastodonProvider> logger) : ISocialMediaProvider
{
    /// <summary>Named <see cref="HttpClient"/> registered by <c>AddMastodonProvider</c>.</summary>
    public const string HttpClientName = "mastodon";

    // Mastodon caps `limit` at 40 for both timelines and notifications.
    private const int PageSize = 40;

    // Bounds a single poll. Ten pages covers a five-minute interval with plenty of headroom while
    // still guaranteeing the loop terminates if a server keeps returning full pages.
    private const int MaxPages = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly MastodonOptions _options = options.Value;

    // The account id never changes for a given token, so it is resolved once instead of costing
    // a verify_credentials round trip on every poll.
    private readonly SemaphoreSlim _accountLock = new(1, 1);
    private MastodonAccount? _account;

    public string ProviderId => "mastodon";
    public string ProviderName => "Mastodon";

    public async Task<bool> ValidateConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = CreateRequest(HttpMethod.Get, "/api/v1/accounts/verify_credentials");
            using var response = await client.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Mastodon connection validation failed");
            return false;
        }
    }

    public async Task<SocialProfile> GetProfileAsync(CancellationToken ct = default)
    {
        // Deliberately not served from the cache: follower and post counts are the point of this call.
        var account = await GetAsync<MastodonAccount>("/api/v1/accounts/verify_credentials", ct)
            ?? throw new InvalidOperationException("Failed to get Mastodon account");
        Volatile.Write(ref _account, account);

        return new SocialProfile
        {
            ProviderId = ProviderId,
            Handle = account.Acct,
            DisplayName = account.DisplayName,
            Bio = HtmlText.ToPlainText(account.Note),
            AvatarUrl = account.Avatar,
            FollowerCount = account.FollowersCount,
            FollowingCount = account.FollowingCount,
            PostCount = account.StatusesCount
        };
    }

    public async Task<IReadOnlyList<SocialPost>> GetRecentPostsAsync(DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var account = await GetAccountAsync(ct);
        var posts = new List<SocialPost>();
        string? maxId = null;

        for (var page = 0; page < MaxPages; page++)
        {
            var url = $"/api/v1/accounts/{account.Id}/statuses?limit={PageSize}";
            if (maxId is not null)
            {
                url += $"&max_id={Uri.EscapeDataString(maxId)}";
            }

            var statuses = await GetAsync<List<MastodonStatus>>(url, ct) ?? [];
            if (statuses.Count == 0)
            {
                break;
            }

            var reachedCutoff = false;
            foreach (var status in statuses)
            {
                if (since is not null && status.CreatedAt < since)
                {
                    reachedCutoff = true;
                    continue;
                }
                posts.Add(MapToSocialPost(status, account.Acct, isOwn: true));
            }

            // No `since` means this is a first-ever poll; one page is backfill enough.
            if (since is null || reachedCutoff || statuses.Count < PageSize)
            {
                break;
            }
            maxId = statuses[^1].Id;
        }

        return posts;
    }

    public async Task<IReadOnlyList<SocialNotification>> GetNotificationsAsync(DateTimeOffset? since = null, CancellationToken ct = default)
    {
        // Fetch the last-read marker to determine read state
        string? lastReadId = null;
        try
        {
            var markers = await GetAsync<MastodonMarkersResponse>(
                "/api/v1/markers?timeline[]=notifications", ct);
            lastReadId = markers?.Notifications?.LastReadId;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to fetch Mastodon notification markers, all will be marked unread");
        }

        var notifications = new List<SocialNotification>();
        string? maxId = null;

        for (var page = 0; page < MaxPages; page++)
        {
            var url = $"/api/v1/notifications?limit={PageSize}";
            if (maxId is not null)
            {
                url += $"&max_id={Uri.EscapeDataString(maxId)}";
            }

            var batch = await GetAsync<List<MastodonNotification>>(url, ct) ?? [];
            if (batch.Count == 0)
            {
                break;
            }

            var reachedCutoff = false;
            foreach (var notification in batch)
            {
                if (since is not null && notification.CreatedAt < since)
                {
                    reachedCutoff = true;
                    continue;
                }
                notifications.Add(MapToSocialNotification(notification, lastReadId));
            }

            if (since is null || reachedCutoff || batch.Count < PageSize)
            {
                break;
            }
            maxId = batch[^1].Id;
        }

        return notifications;
    }

    private async Task<MastodonAccount> GetAccountAsync(CancellationToken ct)
    {
        var cached = Volatile.Read(ref _account);
        if (cached is not null)
        {
            return cached;
        }

        await _accountLock.WaitAsync(ct);
        try
        {
            cached = Volatile.Read(ref _account);
            if (cached is not null)
            {
                return cached;
            }

            var account = await GetAsync<MastodonAccount>("/api/v1/accounts/verify_credentials", ct)
                ?? throw new InvalidOperationException("Failed to get Mastodon account");
            Volatile.Write(ref _account, account);
            return account;
        }
        finally
        {
            _accountLock.Release();
        }
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = CreateRequest(HttpMethod.Get, url);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    // Auth goes on the request, never on HttpClient.DefaultRequestHeaders: the handler is pooled and
    // shared across the polling loop and A2A request threads, and HttpHeaders is not thread-safe.
    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
        return request;
    }

    private SocialPost MapToSocialPost(MastodonStatus status, string ownerAcct, bool isOwn)
    {
        return new SocialPost
        {
            Id = $"mastodon:{status.Id}",
            ProviderId = ProviderId,
            PlatformPostId = status.Id,
            AuthorHandle = status.Account?.Acct ?? ownerAcct,
            Content = HtmlText.ToPlainText(status.Content),
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
            Content = HtmlText.ToPlainText(notification.Status?.Content),
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
