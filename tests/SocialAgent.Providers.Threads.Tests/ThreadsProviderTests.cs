using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
        var accessToken = Environment.GetEnvironmentVariable("THREADS_ACCESS_TOKEN")
            ?? throw new InvalidOperationException(
                "Set THREADS_ACCESS_TOKEN environment variable to run integration tests");

        var options = Options.Create(new ThreadsOptions
        {
            Enabled = true,
            BaseUrl = "https://graph.threads.net",
            AccessToken = accessToken
        });

        var tokenStore = new ThreadsTokenStore(options);

        var httpClient = new HttpClient { BaseAddress = new Uri(options.Value.BaseUrl) };

        return new ThreadsProvider(
            httpClient,
            options,
            tokenStore,
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
