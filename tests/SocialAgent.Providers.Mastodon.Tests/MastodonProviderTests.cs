using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SocialAgent.TestSupport;

namespace SocialAgent.Providers.Mastodon.Tests;

[TestClass]
public class MastodonOptionsTests
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
public class MastodonProviderTests
{
    private const string AccountJson = """
        {"id":"12345","acct":"me@mastodon.test","display_name":"Me","note":"<p>Hi <b>there</b></p>",
        "avatar":"https://cdn.test/a.png","followers_count":42,"following_count":7,"statuses_count":99}
        """;

    private static MastodonProvider CreateProvider(StubHttpMessageHandler handler)
    {
        var options = Options.Create(new MastodonOptions
        {
            Enabled = true,
            InstanceUrl = "https://mastodon.test",
            AccessToken = "token-abc"
        });

        return new MastodonProvider(
            StubHttpClientFactory.For(handler, "https://mastodon.test"),
            options,
            NullLogger<MastodonProvider>.Instance);
    }

    [TestMethod]
    public async Task GetProfile_SendsBearerToken_AndStripsHtmlFromBio()
    {
        var handler = StubHttpMessageHandler.AlwaysJson(AccountJson);
        var provider = CreateProvider(handler);

        var profile = await provider.GetProfileAsync();

        Assert.AreEqual("me@mastodon.test", profile.Handle);
        Assert.AreEqual(42, profile.FollowerCount);
        Assert.AreEqual("Hi there", profile.Bio);
        Assert.AreEqual("Bearer", handler.Requests[0].AuthScheme);
        Assert.AreEqual("token-abc", handler.Requests[0].AuthParameter);
    }

    [TestMethod]
    public async Task AccountId_IsResolvedOnce_AcrossPolls()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
            request.RequestUri!.AbsolutePath.Contains("verify_credentials")
                ? StubHttpMessageHandler.Json(HttpStatusCode.OK, AccountJson)
                : StubHttpMessageHandler.Json(HttpStatusCode.OK, "[]"));
        var provider = CreateProvider(handler);

        await provider.GetRecentPostsAsync();
        await provider.GetRecentPostsAsync();

        var verifyCalls = handler.Requests.Count(r => r.PathAndQuery.Contains("verify_credentials"));
        Assert.AreEqual(1, verifyCalls, "the account id should be cached rather than re-fetched each poll");
    }

    [TestMethod]
    public async Task GetRecentPosts_StripsHtmlContent()
    {
        var posts = StatusesJson(1, DateTimeOffset.UtcNow, content: "<p>Line one<br>Line two</p>");
        var handler = new StubHttpMessageHandler((request, _) =>
            request.RequestUri!.AbsolutePath.Contains("verify_credentials")
                ? StubHttpMessageHandler.Json(HttpStatusCode.OK, AccountJson)
                : StubHttpMessageHandler.Json(HttpStatusCode.OK, posts));
        var provider = CreateProvider(handler);

        var result = await provider.GetRecentPostsAsync();

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Line one\nLine two", result[0].Content);
    }

    [TestMethod]
    public async Task GetRecentPosts_PagesWithMaxId_UntilSinceCutoff()
    {
        var recent = DateTimeOffset.UtcNow;
        var old = DateTimeOffset.UtcNow.AddDays(-10);
        var call = 0;

        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("verify_credentials"))
            {
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, AccountJson);
            }
            // First statuses page is full and recent; second is older than the cutoff.
            var body = call++ == 0
                ? StatusesJson(40, recent)
                : StatusesJson(40, old, startId: 100);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, body);
        });
        var provider = CreateProvider(handler);

        var result = await provider.GetRecentPostsAsync(since: DateTimeOffset.UtcNow.AddDays(-1));

        Assert.AreEqual(40, result.Count, "only posts newer than the cutoff should be returned");
        var second = handler.Requests.Last(r => r.PathAndQuery.Contains("/statuses"));
        Assert.IsTrue(second.PathAndQuery.Contains("max_id="), second.PathAndQuery);
    }

    [TestMethod]
    public async Task GetRecentPosts_WithoutSince_FetchesOnePage()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
            request.RequestUri!.AbsolutePath.Contains("verify_credentials")
                ? StubHttpMessageHandler.Json(HttpStatusCode.OK, AccountJson)
                : StubHttpMessageHandler.Json(HttpStatusCode.OK, StatusesJson(40, DateTimeOffset.UtcNow)));
        var provider = CreateProvider(handler);

        var result = await provider.GetRecentPostsAsync();

        Assert.AreEqual(40, result.Count);
        Assert.AreEqual(1, handler.Requests.Count(r => r.PathAndQuery.Contains("/statuses")),
            "a first-ever poll should not page indefinitely");
    }

    [TestMethod]
    public async Task GetRecentPosts_ExcludesBoosts()
    {
        // A boost comes back as a status with empty content carrying the original author's
        // engagement, so it must be filtered server-side rather than recorded as an own post.
        var handler = new StubHttpMessageHandler((request, _) =>
            request.RequestUri!.AbsolutePath.Contains("verify_credentials")
                ? StubHttpMessageHandler.Json(HttpStatusCode.OK, AccountJson)
                : StubHttpMessageHandler.Json(HttpStatusCode.OK, StatusesJson(1, DateTimeOffset.UtcNow)));
        var provider = CreateProvider(handler);

        await provider.GetRecentPostsAsync();

        var statuses = handler.Requests.Single(r => r.PathAndQuery.Contains("/statuses"));
        StringAssert.Contains(statuses.PathAndQuery, "exclude_reblogs=true");
    }

    private static string StatusesJson(
        int count, DateTimeOffset createdAt, string content = "<p>hello</p>", int startId = 0)
    {
        var statuses = Enumerable.Range(startId, count).Select(i => new
        {
            id = (1000 - i).ToString(),
            content,
            created_at = createdAt,
            url = $"https://mastodon.test/@me/{i}",
            favourites_count = 1,
            reblogs_count = 2,
            replies_count = 3,
            account = new { id = "12345", acct = "me@mastodon.test" }
        });
        return JsonSerializer.Serialize(statuses);
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
        var accessToken = Environment.GetEnvironmentVariable("MASTODON_ACCESS_TOKEN");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            Assert.Inconclusive("Set MASTODON_ACCESS_TOKEN to run Mastodon integration tests.");
        }

        var options = Options.Create(new MastodonOptions
        {
            Enabled = true,
            InstanceUrl = instanceUrl,
            AccessToken = accessToken!
        });

        return new MastodonProvider(
            new LiveHttpClientFactory(instanceUrl),
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
    }

    [TestMethod]
    public async Task GetRecentPosts_WithRealToken_ReturnsPosts()
    {
        var provider = CreateProvider();

        var posts = await provider.GetRecentPostsAsync();

        Assert.IsNotNull(posts);
    }

    [TestMethod]
    public async Task GetNotifications_WithRealToken_ReturnsNotifications()
    {
        var provider = CreateProvider();

        var notifications = await provider.GetNotificationsAsync();

        Assert.IsNotNull(notifications);
    }
}
