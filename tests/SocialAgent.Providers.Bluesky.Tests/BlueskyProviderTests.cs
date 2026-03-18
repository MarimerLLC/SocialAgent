using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace SocialAgent.Providers.Bluesky.Tests;

[TestClass]
public class BlueskyProviderTests
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
[TestCategory("Integration")]
public class BlueskyProviderIntegrationTests
{
    private static BlueskyProvider CreateProvider()
    {
        var handle = Environment.GetEnvironmentVariable("BLUESKY_HANDLE")
            ?? throw new InvalidOperationException(
                "Set BLUESKY_HANDLE environment variable to run integration tests");
        var appPassword = Environment.GetEnvironmentVariable("BLUESKY_APP_PASSWORD")
            ?? throw new InvalidOperationException(
                "Set BLUESKY_APP_PASSWORD environment variable to run integration tests");

        var options = Options.Create(new BlueskyOptions
        {
            Enabled = true,
            ServiceUrl = "https://bsky.social",
            Handle = handle,
            AppPassword = appPassword
        });

        return new BlueskyProvider(
            new HttpClient(),
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
        Console.WriteLine($"Handle: {profile.Handle}");
        Console.WriteLine($"Display Name: {profile.DisplayName}");
        Console.WriteLine($"Followers: {profile.FollowerCount}");
        Console.WriteLine($"Posts: {profile.PostCount}");
    }

    [TestMethod]
    public async Task GetRecentPosts_WithRealCredentials_ReturnsPosts()
    {
        var provider = CreateProvider();

        var posts = await provider.GetRecentPostsAsync();

        Assert.IsNotNull(posts);
        Console.WriteLine($"Retrieved {posts.Count} posts");
        foreach (var post in posts.Take(3))
        {
            Console.WriteLine($"  [{post.CreatedAt:g}] {post.Content?[..Math.Min(80, post.Content.Length)]}...");
        }
    }

    [TestMethod]
    public async Task GetNotifications_WithRealCredentials_ReturnsNotifications()
    {
        var provider = CreateProvider();

        var notifications = await provider.GetNotificationsAsync();

        Assert.IsNotNull(notifications);
        Console.WriteLine($"Retrieved {notifications.Count} notifications");
        foreach (var n in notifications.Take(5))
        {
            Console.WriteLine($"  [{n.CreatedAt:g}] {n.Type} from {n.FromHandle}");
        }
    }
}
