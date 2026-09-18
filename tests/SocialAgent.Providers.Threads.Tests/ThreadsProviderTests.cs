using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SocialAgent.TestSupport;

namespace SocialAgent.Providers.Threads.Tests;

[TestClass]
public class ThreadsOptionsTests
{
    [TestMethod]
    public void ThreadsOptions_Defaults_AreSet()
    {
        var options = new ThreadsOptions();

        Assert.IsFalse(options.Enabled);
        Assert.AreEqual("https://graph.threads.net", options.BaseUrl);
        Assert.AreEqual(string.Empty, options.AccessToken);
        Assert.IsFalse(options.IncludePostInsights);
        Assert.AreEqual(7, options.RefreshThresholdDays);
        Assert.AreEqual(24, options.RefreshCheckIntervalHours);
    }
}

[TestClass]
public class ThreadsTokenStoreTests
{
    [TestMethod]
    public void Current_FallsBackToConfiguredAccessToken_WhenNotSeeded()
    {
        var store = new ThreadsTokenStore(Options.Create(new ThreadsOptions
        {
            AccessToken = "configured-token"
        }));

        var (token, expiresAt) = store.Current;

        Assert.AreEqual("configured-token", token);
        Assert.IsTrue(expiresAt > DateTimeOffset.UtcNow.AddDays(59));
        Assert.IsTrue(expiresAt < DateTimeOffset.UtcNow.AddDays(61));
    }

    [TestMethod]
    public void Current_ReturnsSetValue_WhenSeeded()
    {
        var store = new ThreadsTokenStore(Options.Create(new ThreadsOptions
        {
            AccessToken = "configured-token"
        }));
        var future = DateTimeOffset.UtcNow.AddDays(45);

        store.Set("refreshed-token", future);
        var (token, expiresAt) = store.Current;

        Assert.AreEqual("refreshed-token", token);
        Assert.AreEqual(future, expiresAt);
    }

    [TestMethod]
    public void Current_Throws_WhenNoTokenConfiguredAndNotSeeded()
    {
        var store = new ThreadsTokenStore(Options.Create(new ThreadsOptions()));

        Assert.Throws<InvalidOperationException>(() => _ = store.Current);
    }
}

[TestClass]
[TestCategory("Integration")]
public class ThreadsProviderIntegrationTests
{
    private static ThreadsProvider CreateProvider()
    {
        var accessToken = Environment.GetEnvironmentVariable("THREADS_ACCESS_TOKEN");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            Assert.Inconclusive("Set THREADS_ACCESS_TOKEN to run Threads integration tests.");
        }

        var options = Options.Create(new ThreadsOptions
        {
            Enabled = true,
            BaseUrl = "https://graph.threads.net",
            AccessToken = accessToken!
        });

        return new ThreadsProvider(
            new LiveHttpClientFactory(options.Value.BaseUrl),
            options,
            new ThreadsTokenStore(options),
            NullLogger<ThreadsProvider>.Instance);
    }

    [TestMethod]
    public async Task ValidateConnection_WithRealToken_ReturnsTrue()
    {
        var provider = CreateProvider();

        var result = await provider.ValidateConnectionAsync();

        Assert.IsTrue(result, "Connection to Threads should succeed with a valid token");
    }

    [TestMethod]
    public async Task GetProfile_WithRealToken_ReturnsProfile()
    {
        var provider = CreateProvider();

        var profile = await provider.GetProfileAsync();

        Assert.IsNotNull(profile);
        Assert.AreEqual("threads", profile.ProviderId);
        Assert.IsFalse(string.IsNullOrEmpty(profile.Handle), "Handle should not be empty");
        Console.WriteLine($"Handle: {profile.Handle}");
        Console.WriteLine($"Display Name: {profile.DisplayName}");
        Console.WriteLine($"Followers: {profile.FollowerCount}");
    }

    [TestMethod]
    public async Task GetRecentPosts_WithRealToken_ReturnsPosts()
    {
        var provider = CreateProvider();

        var posts = await provider.GetRecentPostsAsync();

        Assert.IsNotNull(posts);
        Console.WriteLine($"Retrieved {posts.Count} posts");
        foreach (var post in posts.Take(3))
        {
            Console.WriteLine($"  [{post.PlatformPostId}] {post.Content?[..Math.Min(80, post.Content.Length)]}");
        }
    }

    [TestMethod]
    public async Task GetNotifications_WithRealToken_ReturnsList()
    {
        var provider = CreateProvider();

        var notifications = await provider.GetNotificationsAsync();

        Assert.IsNotNull(notifications);
        Console.WriteLine($"Retrieved {notifications.Count} notifications");
    }
}

[TestClass]
public class ThreadsProviderHttpTests
{
    private const string UserJson = """
        {"id":"u1","username":"me","name":"Me","threads_biography":"bio"}
        """;

    private static (ThreadsProvider Provider, ThreadsTokenStore Store) CreateProvider(StubHttpMessageHandler handler)
    {
        var options = Options.Create(new ThreadsOptions
        {
            Enabled = true,
            BaseUrl = "https://graph.threads.test",
            AccessToken = "token-1"
        });
        var store = new ThreadsTokenStore(options);
        var provider = new ThreadsProvider(
            StubHttpClientFactory.For(handler, "https://graph.threads.test"),
            options,
            store,
            NullLogger<ThreadsProvider>.Instance);
        return (provider, store);
    }

    [TestMethod]
    public async Task GetProfile_SendsTokenAsBearerHeader_NotInQueryString()
    {
        var handler = StubHttpMessageHandler.AlwaysJson(UserJson);
        var (provider, _) = CreateProvider(handler);

        await provider.GetProfileAsync();

        var request = handler.Requests[0];
        Assert.AreEqual("Bearer", request.AuthScheme);
        Assert.AreEqual("token-1", request.AuthParameter);
        Assert.IsFalse(request.PathAndQuery.Contains("access_token="),
            "the access token must not appear in the URL, which reaches traces and logs");
    }

    [TestMethod]
    public async Task RefreshToken_UsesAuthorizationHeader_WhenAccepted()
    {
        var handler = StubHttpMessageHandler.AlwaysJson(
            """{"access_token":"token-2","token_type":"bearer","expires_in":5184000}""");
        var (provider, store) = CreateProvider(handler);

        var refreshed = await provider.RefreshTokenAsync();

        Assert.IsNotNull(refreshed);
        Assert.AreEqual("token-2", refreshed.Value.Token);
        Assert.AreEqual("token-2", store.Current.Token);
        Assert.AreEqual(1, handler.Requests.Count);
        Assert.AreEqual("token-1", handler.Requests[0].AuthParameter);
        Assert.IsFalse(handler.Requests[0].PathAndQuery.Contains("access_token="),
            "the header form must not also put the token in the URL");
    }

    [TestMethod]
    public async Task RefreshToken_FallsBackToQueryParameter_WhenHeaderRejected()
    {
        var handler = StubHttpMessageHandler.Sequence(
            () => StubHttpMessageHandler.Status(HttpStatusCode.BadRequest),
            () => StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"access_token":"token-2","token_type":"bearer","expires_in":5184000}"""));
        var (provider, store) = CreateProvider(handler);

        var refreshed = await provider.RefreshTokenAsync();

        Assert.IsNotNull(refreshed);
        Assert.AreEqual("token-2", store.Current.Token);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.IsTrue(handler.Requests[1].PathAndQuery.Contains("access_token=token-1"),
            "the documented query-parameter form is the fallback");
    }

    [TestMethod]
    public async Task RefreshToken_ReturnsNull_AndKeepsToken_WhenBothFormsFail()
    {
        var handler = StubHttpMessageHandler.Sequence(
            () => StubHttpMessageHandler.Status(HttpStatusCode.BadRequest),
            () => StubHttpMessageHandler.Status(HttpStatusCode.BadRequest));
        var (provider, store) = CreateProvider(handler);

        var refreshed = await provider.RefreshTokenAsync();

        Assert.IsNull(refreshed);
        Assert.AreEqual("token-1", store.Current.Token, "a failed refresh must not clobber the working token");
    }

    [TestMethod]
    public async Task GetRecentPosts_FollowsAfterCursor()
    {
        var call = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var body = call++ == 0
                ? ThreadsPage(25, after: "cursor-1")
                : ThreadsPage(3, after: null);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, body);
        });
        var (provider, _) = CreateProvider(handler);

        var posts = await provider.GetRecentPostsAsync();

        Assert.AreEqual(28, posts.Count);
        Assert.IsTrue(handler.Requests[1].PathAndQuery.Contains("after=cursor-1"),
            handler.Requests[1].PathAndQuery);
    }

    [TestMethod]
    public async Task GetNotifications_Tolerates_MissingScopes()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Status(HttpStatusCode.Forbidden));
        var (provider, _) = CreateProvider(handler);

        var notifications = await provider.GetNotificationsAsync();

        Assert.AreEqual(0, notifications.Count, "a missing scope should degrade, not throw");
    }

    private static string ThreadsPage(int count, string? after)
    {
        var data = Enumerable.Range(0, count).Select(i => new
        {
            id = $"t{i}",
            text = $"post {i}",
            timestamp = DateTimeOffset.UtcNow,
            permalink = $"https://threads.test/{i}",
            username = "me",
            replies_count = 1,
            reposts_count = 2,
            quotes_count = 3
        });
        return JsonSerializer.Serialize(new
        {
            data,
            paging = new { cursors = new { after } }
        });
    }
}
