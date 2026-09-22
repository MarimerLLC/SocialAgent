using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SocialAgent.TestSupport;

namespace SocialAgent.Providers.Bluesky.Tests;

[TestClass]
public class BlueskyOptionsTests
{
    [TestMethod]
    public void BlueskyOptions_DefaultServiceUrl_IsSet()
    {
        var options = new BlueskyOptions();

        Assert.AreEqual("https://bsky.social", options.ServiceUrl);
        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(string.Empty, options.Handle);
        Assert.AreEqual(string.Empty, options.AppPassword);
    }
}

[TestClass]
public class BlueskyProviderTests
{
    private const string SessionJson = """
        {"accessJwt":"access-1","refreshJwt":"refresh-1","did":"did:plc:abc","handle":"me.bsky.social"}
        """;

    private const string RefreshedSessionJson = """
        {"accessJwt":"access-2","refreshJwt":"refresh-2","did":"did:plc:abc","handle":"me.bsky.social"}
        """;

    private const string ProfileJson = """
        {"did":"did:plc:abc","handle":"me.bsky.social","displayName":"Me","followersCount":7,"followsCount":3,"postsCount":11}
        """;

    private static BlueskyProvider CreateProvider(StubHttpMessageHandler handler)
    {
        var options = Options.Create(new BlueskyOptions
        {
            Enabled = true,
            ServiceUrl = "https://bsky.test",
            Handle = "me.bsky.social",
            AppPassword = "app-password"
        });

        return new BlueskyProvider(
            StubHttpClientFactory.For(handler, "https://bsky.test"),
            options,
            NullLogger<BlueskyProvider>.Instance);
    }

    [TestMethod]
    public async Task GetProfile_LogsInAndSendsAccessJwt()
    {
        var handler = StubHttpMessageHandler.Sequence(
            () => StubHttpMessageHandler.Json(HttpStatusCode.OK, SessionJson),
            () => StubHttpMessageHandler.Json(HttpStatusCode.OK, ProfileJson));
        var provider = CreateProvider(handler);

        var profile = await provider.GetProfileAsync();

        Assert.AreEqual("me.bsky.social", profile.Handle);
        Assert.AreEqual(7, profile.FollowerCount);

        var login = handler.Requests[0];
        Assert.IsTrue(login.PathAndQuery.Contains("createSession"), login.PathAndQuery);

        var profileCall = handler.Requests[1];
        Assert.AreEqual("Bearer", profileCall.AuthScheme);
        Assert.AreEqual("access-1", profileCall.AuthParameter);
        Assert.IsTrue(profileCall.PathAndQuery.Contains("did%3Aplc%3Aabc"), profileCall.PathAndQuery);
    }

    [TestMethod]
    public async Task ExpiredAccessJwt_IsRefreshedWithRefreshJwt_AndRequestRetried()
    {
        // login -> 401 -> refreshSession -> retry
        var handler = StubHttpMessageHandler.Sequence(
            () => StubHttpMessageHandler.Json(HttpStatusCode.OK, SessionJson),
            () => StubHttpMessageHandler.Status(HttpStatusCode.Unauthorized),
            () => StubHttpMessageHandler.Json(HttpStatusCode.OK, RefreshedSessionJson),
            () => StubHttpMessageHandler.Json(HttpStatusCode.OK, ProfileJson));
        var provider = CreateProvider(handler);

        var profile = await provider.GetProfileAsync();

        Assert.AreEqual("me.bsky.social", profile.Handle);

        var refresh = handler.Requests[2];
        Assert.IsTrue(refresh.PathAndQuery.Contains("refreshSession"), refresh.PathAndQuery);
        Assert.AreEqual("refresh-1", refresh.AuthParameter, "refresh must present the refresh JWT");

        var retry = handler.Requests[3];
        Assert.AreEqual("access-2", retry.AuthParameter, "retry must use the newly issued access JWT");
    }

    [TestMethod]
    public async Task FailedRefresh_FallsBackToFullLogin()
    {
        // login -> 401 -> refreshSession fails -> createSession -> retry
        var handler = StubHttpMessageHandler.Sequence(
            () => StubHttpMessageHandler.Json(HttpStatusCode.OK, SessionJson),
            () => StubHttpMessageHandler.Status(HttpStatusCode.Unauthorized),
            () => StubHttpMessageHandler.Status(HttpStatusCode.BadRequest),
            () => StubHttpMessageHandler.Json(HttpStatusCode.OK, RefreshedSessionJson),
            () => StubHttpMessageHandler.Json(HttpStatusCode.OK, ProfileJson));
        var provider = CreateProvider(handler);

        var profile = await provider.GetProfileAsync();

        Assert.AreEqual("me.bsky.social", profile.Handle);
        Assert.IsTrue(handler.Requests[3].PathAndQuery.Contains("createSession"),
            "a rejected refresh should fall back to a full login");
        Assert.AreEqual("access-2", handler.Requests[4].AuthParameter);
    }

    [TestMethod]
    public async Task SessionIsReusedAcrossCalls()
    {
        var handler = StubHttpMessageHandler.Sequence(
            () => StubHttpMessageHandler.Json(HttpStatusCode.OK, SessionJson),
            () => StubHttpMessageHandler.Json(HttpStatusCode.OK, ProfileJson));
        var provider = CreateProvider(handler);

        await provider.GetProfileAsync();
        await provider.GetProfileAsync();

        var logins = handler.Requests.Count(r => r.PathAndQuery.Contains("createSession"));
        Assert.AreEqual(1, logins, "a cached session should not re-authenticate on every call");
    }

    [TestMethod]
    public async Task GetRecentPosts_FollowsCursor_UntilSinceCutoff()
    {
        var recent = DateTimeOffset.UtcNow;
        var old = DateTimeOffset.UtcNow.AddDays(-5);
        var page1 = BuildFeed(50, recent, cursor: "cursor-1");
        var page2 = BuildFeed(1, old, cursor: null);

        var handler = StubHttpMessageHandler.Sequence(
            () => StubHttpMessageHandler.Json(HttpStatusCode.OK, SessionJson),
            () => StubHttpMessageHandler.Json(HttpStatusCode.OK, page1),
            () => StubHttpMessageHandler.Json(HttpStatusCode.OK, page2));
        var provider = CreateProvider(handler);

        var posts = await provider.GetRecentPostsAsync(since: DateTimeOffset.UtcNow.AddDays(-1));

        Assert.AreEqual(50, posts.Count, "only posts newer than the cutoff should be returned");
        Assert.IsTrue(handler.Requests[2].PathAndQuery.Contains("cursor=cursor-1"),
            handler.Requests[2].PathAndQuery);
    }

    [TestMethod]
    public async Task GetRecentPosts_WithoutSince_FetchesOnePage()
    {
        var handler = StubHttpMessageHandler.Sequence(
            () => StubHttpMessageHandler.Json(HttpStatusCode.OK, SessionJson),
            () => StubHttpMessageHandler.Json(HttpStatusCode.OK, BuildFeed(50, DateTimeOffset.UtcNow, "cursor-1")));
        var provider = CreateProvider(handler);

        var posts = await provider.GetRecentPostsAsync();

        Assert.AreEqual(50, posts.Count);
        Assert.AreEqual(2, handler.Requests.Count, "a first-ever poll should not page indefinitely");
    }

    [TestMethod]
    public async Task MapsRecordCreatedAt_NotIndexedAt()
    {
        var authored = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var indexed = authored.AddHours(6);
        var feed = FeedJson(
            [BuildPost("xyz", "cid1", text: "hello", createdAt: authored, indexedAt: indexed)],
            cursor: null);

        var handler = StubHttpMessageHandler.Sequence(
            () => StubHttpMessageHandler.Json(HttpStatusCode.OK, SessionJson),
            () => StubHttpMessageHandler.Json(HttpStatusCode.OK, feed));
        var provider = CreateProvider(handler);

        var posts = await provider.GetRecentPostsAsync();

        Assert.AreEqual(1, posts.Count);
        Assert.AreEqual(authored, posts[0].CreatedAt);
        Assert.AreEqual("https://bsky.app/profile/me.bsky.social/post/xyz", posts[0].Url);
    }

    [TestMethod]
    public async Task GetRecentPosts_ExcludesReposts()
    {
        // getAuthorFeed mixes in the account's reposts, which carry the original author and their
        // engagement. Counting those as own posts inflated every engagement figure.
        var now = DateTimeOffset.UtcNow;
        var feed = FeedJson(
            [
                BuildPost("mine", "cid-mine", "my own post", now, now),
                BuildPost("theirs", "cid-theirs", "a viral post", now, now,
                    authorDid: "did:plc:someoneelse", authorHandle: "markhamillofficial.bsky.social")
            ],
            cursor: null);

        var handler = StubHttpMessageHandler.Sequence(
            () => StubHttpMessageHandler.Json(HttpStatusCode.OK, SessionJson),
            () => StubHttpMessageHandler.Json(HttpStatusCode.OK, feed));
        var provider = CreateProvider(handler);

        var posts = await provider.GetRecentPostsAsync();

        Assert.AreEqual(1, posts.Count, "a repost by another author must not be recorded");
        Assert.AreEqual("my own post", posts[0].Content);
        Assert.AreEqual("me.bsky.social", posts[0].AuthorHandle);
    }

    private static string BuildFeed(int count, DateTimeOffset indexedAt, string? cursor)
    {
        var posts = Enumerable.Range(0, count)
            .Select(i => BuildPost($"p{i}", $"cid{i}", $"post {i}", indexedAt, indexedAt))
            .ToArray();
        return FeedJson(posts, cursor);
    }

    private static object BuildPost(
        string rkey, string cid, string text, DateTimeOffset createdAt, DateTimeOffset indexedAt,
        string authorDid = "did:plc:abc", string authorHandle = "me.bsky.social") => new
        {
            post = new
            {
                uri = $"at://{authorDid}/app.bsky.feed.post/{rkey}",
                cid,
                author = new { did = authorDid, handle = authorHandle },
                record = new { text, createdAt },
                likeCount = 1,
                repostCount = 2,
                replyCount = 3,
                indexedAt
            }
        };

    private static string FeedJson(object[] posts, string? cursor) =>
        JsonSerializer.Serialize(new { feed = posts, cursor });
}

[TestClass]
[TestCategory("Integration")]
public class BlueskyProviderIntegrationTests
{
    private static BlueskyProvider CreateProvider()
    {
        var handle = Environment.GetEnvironmentVariable("BLUESKY_HANDLE");
        var appPassword = Environment.GetEnvironmentVariable("BLUESKY_APP_PASSWORD");
        if (string.IsNullOrWhiteSpace(handle) || string.IsNullOrWhiteSpace(appPassword))
        {
            Assert.Inconclusive("Set BLUESKY_HANDLE and BLUESKY_APP_PASSWORD to run Bluesky integration tests.");
        }

        var options = Options.Create(new BlueskyOptions
        {
            Enabled = true,
            ServiceUrl = "https://bsky.social",
            Handle = handle!,
            AppPassword = appPassword!
        });

        return new BlueskyProvider(
            new LiveHttpClientFactory(options.Value.ServiceUrl),
            options,
            NullLogger<BlueskyProvider>.Instance);
    }

    [TestMethod]
    public async Task ValidateConnection_WithRealCredentials_ReturnsTrue()
    {
        var provider = CreateProvider();

        var result = await provider.ValidateConnectionAsync();

        Assert.IsTrue(result, "Connection to Bluesky should succeed with valid credentials");
    }

    [TestMethod]
    public async Task GetProfile_WithRealCredentials_ReturnsProfile()
    {
        var provider = CreateProvider();

        var profile = await provider.GetProfileAsync();

        Assert.IsNotNull(profile);
        Assert.AreEqual("bluesky", profile.ProviderId);
        Assert.IsFalse(string.IsNullOrEmpty(profile.Handle), "Handle should not be empty");
    }

    [TestMethod]
    public async Task GetRecentPosts_WithRealCredentials_ReturnsPosts()
    {
        var provider = CreateProvider();

        var posts = await provider.GetRecentPostsAsync();

        Assert.IsNotNull(posts);
    }

    [TestMethod]
    public async Task GetNotifications_WithRealCredentials_ReturnsNotifications()
    {
        var provider = CreateProvider();

        var notifications = await provider.GetNotificationsAsync();

        Assert.IsNotNull(notifications);
    }
}
