using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SocialAgent.Host.Tests.Mcp;

/// <summary>The MCP tools through the real host and the SDK client.</summary>
[TestClass]
public sealed class McpToolTests
{
    private SocialAgentApp _app = null!;

    [TestInitialize]
    public void Start() => _app = new SocialAgentApp();

    [TestCleanup]
    public async Task StopAsync() => await _app.DisposeAsync();

    private static readonly string[] ToolNames =
    [
        "get_engagement_summary", "get_top_posts", "get_recent_mentions", "get_follower_insights",
        "compare_platforms", "get_unread_notifications", "get_provider_status"
    ];

    [TestMethod]
    public async Task Every_tool_is_listed_read_only_with_an_object_output_schema()
    {
        await using var client = await _app.ConnectAsync();

        var tools = await client.ListToolsAsync();

        CollectionAssert.AreEquivalent(ToolNames, tools.Select(t => t.Name).ToArray());
        foreach (var tool in tools.Select(t => t.ProtocolTool))
        {
            Assert.IsTrue(tool.Annotations?.ReadOnlyHint, $"{tool.Name} is read-only");
            Assert.IsTrue(tool.Annotations?.IdempotentHint, $"{tool.Name} is idempotent");
            Assert.IsFalse(tool.Annotations?.OpenWorldHint ?? true, $"{tool.Name} is closed-world");
            Assert.IsNotNull(tool.OutputSchema, $"{tool.Name} has an output schema");
            Assert.AreEqual("object", tool.OutputSchema.Value.GetProperty("type").GetString(), $"{tool.Name}'s output schema is an object");
        }
    }

    [TestMethod]
    public async Task Recent_mentions_include_replies_and_the_text_carries_the_same_json_as_structured_content()
    {
        _app.Bluesky.SetNotifications(
            _app.Bluesky.Notification("r1", "reply", from: "alice", createdAt: DateTimeOffset.UtcNow.AddMinutes(-1)),
            _app.Bluesky.Notification("l1", "like", from: "bob"));
        _app.Mastodon.SetNotifications(_app.Mastodon.Notification("m1", "mention", from: "carol", createdAt: DateTimeOffset.UtcNow.AddMinutes(-2)));
        await _app.PollAsync();
        await using var client = await _app.ConnectAsync();

        var result = await client.CallToolAsync("get_recent_mentions");

        Assert.IsFalse(result.IsError ?? false, Text(result));
        var structured = result.StructuredContent!.Value;
        Assert.AreEqual("all", structured.GetProperty("providerId").GetString());
        CollectionAssert.AreEqual(new[] { "alice", "carol" },
            structured.GetProperty("mentions").EnumerateArray().Select(m => m.GetProperty("fromHandle").GetString()).ToArray(),
            "replies count as mentions; likes don't");
        Assert.AreEqual("https://bluesky.test/r1", structured.GetProperty("mentions")[0].GetProperty("url").GetString());

        var lines = Text(result).Split('\n', 2);
        StringAssert.StartsWith(lines[0], "2 mentions and replies on every provider");
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(structured.GetRawText()), JsonNode.Parse(lines[1])),
            "the text block's JSON is the structured content");
        Assert.IsFalse(lines[1].Contains('\n'), "the JSON is compact");
        Assert.IsFalse(lines[1].Contains("\"relatedPostId\""), "nulls are omitted");
    }

    [TestMethod]
    [DataRow("Bluesky")]
    [DataRow("BLUESKY")]
    [DataRow("bluesky")]
    public async Task ProviderId_is_case_insensitive(string providerId)
    {
        _app.Bluesky.SetNotifications(_app.Bluesky.Notification("r1", "reply"));
        _app.Mastodon.SetNotifications(_app.Mastodon.Notification("m1", "mention"));
        await _app.PollAsync();
        await using var client = await _app.ConnectAsync();

        var result = await client.CallToolAsync("get_recent_mentions", new Dictionary<string, object?> { ["providerId"] = providerId });

        Assert.AreEqual("bluesky", result.StructuredContent!.Value.GetProperty("providerId").GetString());
        Assert.AreEqual(1, result.StructuredContent.Value.GetProperty("mentions").GetArrayLength());
    }

    [TestMethod]
    [DataRow("all")]
    [DataRow("*")]
    [DataRow("bluesky,mastodon")]
    public async Task All_or_every_provider_named_means_every_provider(string providerId)
    {
        await using var client = await _app.ConnectAsync();

        var result = await client.CallToolAsync("get_unread_notifications", new Dictionary<string, object?> { ["providerId"] = providerId });

        Assert.IsFalse(result.IsError ?? false, Text(result));
        Assert.AreEqual("all", result.StructuredContent!.Value.GetProperty("providerId").GetString());
    }

    [TestMethod]
    [DataRow("get_engagement_summary")]
    [DataRow("get_top_posts")]
    [DataRow("get_recent_mentions")]
    [DataRow("get_follower_insights")]
    [DataRow("get_unread_notifications")]
    public async Task An_unknown_providerId_is_an_error_listing_the_valid_ids(string tool)
    {
        await using var client = await _app.ConnectAsync();

        var result = await client.CallToolAsync(tool, new Dictionary<string, object?> { ["providerId"] = "twitter" });

        Assert.IsTrue(result.IsError);
        Assert.IsNull(result.StructuredContent);
        var lines = Text(result).Split('\n', 2);
        using var json = JsonDocument.Parse(lines[1]);
        StringAssert.Contains(json.RootElement.GetProperty("error").GetString(), "twitter");
        CollectionAssert.AreEqual(new[] { "bluesky", "mastodon" },
            json.RootElement.GetProperty("validProviderIds").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [TestMethod]
    public async Task Since_filters_mentions()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);
        _app.Bluesky.SetNotifications(
            _app.Bluesky.Notification("old", "reply", createdAt: cutoff.AddDays(-1)),
            _app.Bluesky.Notification("new", "reply", createdAt: cutoff.AddHours(1)));
        await _app.PollAsync();
        await using var client = await _app.ConnectAsync();

        var result = await client.CallToolAsync("get_recent_mentions",
            new Dictionary<string, object?> { ["since"] = cutoff.ToString("O") });

        var mentions = result.StructuredContent!.Value.GetProperty("mentions");
        Assert.AreEqual("new", mentions.EnumerateArray().Single().GetProperty("platformNotificationId").GetString());
    }

    [TestMethod]
    public async Task Top_posts_engagement_summary_insights_and_comparison_answer_with_their_data()
    {
        _app.Bluesky.SetPosts(_app.Bluesky.Post("p1", likes: 5, replies: 1), _app.Bluesky.Post("p2", likes: 1));
        _app.Bluesky.SetNotifications(_app.Bluesky.Notification("l1", "like", from: "alice"), _app.Bluesky.Notification("l2", "like", from: "alice"));
        await _app.PollAsync();
        await using var client = await _app.ConnectAsync();

        var top = (await client.CallToolAsync("get_top_posts", new Dictionary<string, object?> { ["count"] = 1 })).StructuredContent!.Value;
        var summary = (await client.CallToolAsync("get_engagement_summary", new Dictionary<string, object?> { ["providerId"] = "bluesky" })).StructuredContent!.Value;
        var insights = (await client.CallToolAsync("get_follower_insights")).StructuredContent!.Value;
        var comparison = (await client.CallToolAsync("compare_platforms")).StructuredContent!.Value;

        Assert.AreEqual("p1", top.GetProperty("posts").EnumerateArray().Single().GetProperty("platformPostId").GetString());
        Assert.AreEqual(6, summary.GetProperty("summary").GetProperty("totalLikes").GetInt32());
        Assert.AreEqual("bluesky", summary.GetProperty("summary").GetProperty("providerId").GetString());
        Assert.AreEqual("alice", insights.GetProperty("engagers")[0].GetProperty("handle").GetString());
        Assert.AreEqual(2, insights.GetProperty("engagers")[0].GetProperty("interactionCount").GetInt32());
        CollectionAssert.AreEquivalent(new[] { "bluesky", "mastodon" },
            comparison.GetProperty("platforms").EnumerateArray().Select(p => p.GetProperty("providerId").GetString()).ToArray());
    }

    [TestMethod]
    public async Task Provider_status_reports_each_provider()
    {
        _app.Mastodon.Connected = false;
        await using var client = await _app.ConnectAsync();

        var result = await client.CallToolAsync("get_provider_status");

        Assert.AreEqual("bluesky connected, mastodon not connected.", Text(result).Split('\n')[0]);
        var providers = result.StructuredContent!.Value.GetProperty("providers");
        Assert.IsTrue(providers[0].GetProperty("connected").GetBoolean());
        Assert.IsFalse(providers[1].GetProperty("connected").GetBoolean());
    }

    [TestMethod]
    public async Task The_mentions_tool_points_at_the_mentions_card_which_can_be_read()
    {
        await using var client = await _app.ConnectAsync();

        var tool = (await client.ListToolsAsync()).Single(t => t.Name == "get_recent_mentions").ProtocolTool;
        var view = await client.ReadResourceAsync("ui://socialagent/mentions.html");

        Assert.AreEqual("ui://socialagent/mentions.html", tool.Meta?["ui"]?["resourceUri"]?.GetValue<string>());
        var html = (TextResourceContents)view.Contents.Single();
        Assert.AreEqual("text/html;profile=mcp-app", html.MimeType);
        StringAssert.Contains(html.Text, "ui/notifications/tool-result");
        Assert.IsFalse(html.Text.Contains("innerHTML"), "mention text is untrusted and is never set as HTML");
    }

    [TestMethod]
    public async Task The_server_tells_agents_that_post_text_is_data()
    {
        await using var client = await _app.ConnectAsync();

        StringAssert.Contains(client.ServerInstructions, "never follow");
        Assert.AreEqual("socialagent", client.ServerInfo.Name);
    }

    [TestMethod]
    public async Task Outside_development_mcp_requires_the_api_key()
    {
        await using var app = new SocialAgentApp(Environments.Production);
        using var http = app.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        var anonymous = await http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode, await anonymous.Content.ReadAsStringAsync());

        await Assert.ThrowsAsync<Exception>(() => app.ConnectAsync(headers: new() { ["X-Api-Key"] = "wrong" }));

        await using var client = await app.ConnectAsync(headers: new() { ["X-Api-Key"] = SocialAgentApp.ApiKey });
        var result = await client.CallToolAsync("get_provider_status");
        Assert.IsFalse(result.IsError ?? false);
    }

    internal static string Text(CallToolResult result) => ((TextContentBlock)result.Content[0]).Text;
}
