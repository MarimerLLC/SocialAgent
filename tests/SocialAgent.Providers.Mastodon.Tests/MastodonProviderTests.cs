using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace SocialAgent.Providers.Mastodon.Tests;

[TestClass]
public class MastodonProviderTests
{
    [TestMethod]
    public void MastodonOptions_DefaultInstanceUrl_IsSet()
    {
        var options = new MastodonOptions();

        Assert.AreEqual("https://mastodon.social", options.InstanceUrl);
        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(string.Empty, options.AccessToken);
    }
}

[TestClass]
[TestCategory("Integration")]
public class MastodonProviderIntegrationTests
{
    private static MastodonProvider CreateProvider()
    {
        var instanceUrl = Environment.GetEnvironmentVariable("MASTODON_INSTANCE_URL")
            ?? "https://mastodon.social";
        var accessToken = Environment.GetEnvironmentVariable("MASTODON_ACCESS_TOKEN")
            ?? throw new InvalidOperationException(
                "Set MASTODON_ACCESS_TOKEN environment variable to run integration tests");

        var options = Options.Create(new MastodonOptions
        {
            Enabled = true,
            InstanceUrl = instanceUrl,
            AccessToken = accessToken
        });

        return new MastodonProvider(
            new HttpClient(),
            options,
            NullLogger<MastodonProvider>.Instance);
    }

    [TestMethod]
    public async Task ValidateConnection_WithRealToken_ReturnsTrue()
    {
        var provider = CreateProvider();

        var result = await provider.ValidateConnectionAsync();

        Assert.IsTrue(result, "Connection to Mastodon should succeed with a valid token");
    }

    [TestMethod]
    public async Task GetProfile_WithRealToken_ReturnsProfile()
    {
        var provider = CreateProvider();

        var profile = await provider.GetProfileAsync();

        Assert.IsNotNull(profile);
        Assert.AreEqual("mastodon", profile.ProviderId);
        Assert.IsFalse(string.IsNullOrEmpty(profile.Handle), "Handle should not be empty");
        Console.WriteLine($"Handle: {profile.Handle}");
        Console.WriteLine($"Display Name: {profile.DisplayName}");
        Console.WriteLine($"Followers: {profile.FollowerCount}");
        Console.WriteLine($"Posts: {profile.PostCount}");
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
            Console.WriteLine($"  [{post.CreatedAt:g}] {post.Content?[..Math.Min(80, post.Content.Length)]}...");
        }
    }

    [TestMethod]
    public async Task GetNotifications_WithRealToken_ReturnsNotifications()
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
