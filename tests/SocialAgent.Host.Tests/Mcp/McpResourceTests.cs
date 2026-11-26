using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SocialAgent.Host.Mcp;

namespace SocialAgent.Host.Tests.Mcp;

/// <summary>The <c>social://</c> resources through the real host and the SDK client.</summary>
[TestClass]
public sealed class McpResourceTests
{
    private SocialAgentApp _app = null!;

    [TestInitialize]
    public void Start() => _app = new SocialAgentApp();

    [TestCleanup]
    public async Task StopAsync() => await _app.DisposeAsync();

    [TestMethod]
    public async Task Resources_and_templates_are_listed_and_subscribe_is_advertised()
    {
        await using var client = await _app.ConnectAsync();

        var resources = await client.ListResourcesAsync();
        var templates = await client.ListResourceTemplatesAsync();

        CollectionAssert.IsSubsetOf(
            new[] { "social://providers", "social://mentions", "social://notifications/unread" },
            resources.Select(r => r.Uri).ToArray());
        CollectionAssert.IsSubsetOf(
            new[] { "social://mentions/{providerId}", "social://posts/{providerId}/{postId}" },
            templates.Select(t => t.UriTemplate).ToArray());
        Assert.IsTrue(client.ServerCapabilities.Resources?.Subscribe);
    }

    [TestMethod]
    public async Task Each_resource_reads_as_json_with_a_marker()
    {
        _app.Bluesky.SetPosts(_app.Bluesky.Post("at://did:plc:me/app.bsky.feed.post/3abc", likes: 2));
        _app.Bluesky.SetNotifications(_app.Bluesky.Notification("r1", "reply"), _app.Bluesky.Notification("l1", "like"));
        await _app.PollAsync();
        await using var client = await _app.ConnectAsync();

        var providers = await ReadAsync(client, "social://providers");
        var mentions = await ReadAsync(client, "social://mentions");
        var bluesky = await ReadAsync(client, "social://mentions/Bluesky");
        var unread = await ReadAsync(client, "social://notifications/unread");
        var post = await ReadAsync(client, SocialResourceUri.Post("bluesky", "at://did:plc:me/app.bsky.feed.post/3abc"));

        foreach (var doc in new[] { providers, mentions, bluesky, unread, post })
        {
            Assert.AreEqual(16, doc.GetProperty("marker").GetString()!.Length);
        }
        Assert.AreEqual(2, providers.GetProperty("providers").GetArrayLength());
        Assert.AreEqual("all", mentions.GetProperty("providerId").GetString());
        Assert.AreEqual("r1", mentions.GetProperty("mentions").EnumerateArray().Single().GetProperty("platformNotificationId").GetString());
        Assert.AreEqual("bluesky", bluesky.GetProperty("providerId").GetString(), "the provider is resolved case-insensitively");
        Assert.AreEqual(2, unread.GetProperty("count").GetInt32());
        Assert.AreEqual(2, post.GetProperty("post").GetProperty("likeCount").GetInt32());
    }

    [TestMethod]
    public async Task The_marker_is_stable_across_reads_and_moves_when_the_content_does()
    {
        _app.Bluesky.SetNotifications(_app.Bluesky.Notification("r1", "reply"));
        await _app.PollAsync();
        await using var client = await _app.ConnectAsync();

        var first = Marker(await ReadAsync(client, "social://mentions"));
        var again = Marker(await ReadAsync(client, "social://mentions"));
        _app.Bluesky.SetNotifications(_app.Bluesky.Notification("r1", "reply"), _app.Bluesky.Notification("r2", "reply"));
        await _app.PollAsync();
        var after = Marker(await ReadAsync(client, "social://mentions"));

        Assert.AreEqual(first, again);
        Assert.AreNotEqual(first, after);
    }

    [TestMethod]
    public async Task An_unknown_provider_or_post_is_an_error()
    {
        await using var client = await _app.ConnectAsync();

        var provider = await Assert.ThrowsAsync<McpException>(() => client.ReadResourceAsync("social://mentions/twitter").AsTask());
        await Assert.ThrowsAsync<McpException>(() => client.ReadResourceAsync("social://posts/bluesky/nope").AsTask());

        StringAssert.Contains(provider.Message, "bluesky, mastodon");
    }

    private static async Task<JsonElement> ReadAsync(McpClient client, string uri)
    {
        var result = await client.ReadResourceAsync(uri);
        var text = (TextResourceContents)result.Contents.Single();
        Assert.AreEqual("application/json", text.MimeType);
        return JsonDocument.Parse(text.Text).RootElement.Clone();
    }

    private static string Marker(JsonElement doc) => doc.GetProperty("marker").GetString()!;
}
