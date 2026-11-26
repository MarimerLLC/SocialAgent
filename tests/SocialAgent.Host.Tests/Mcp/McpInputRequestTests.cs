using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SocialAgent.Host.Tests.Mcp;

/// <summary>
/// MRTR input requests: what a tool asks when <c>since</c> isn't a timestamp, what it does with the
/// answer, and that clients that can't be asked get the default window with a notice.
/// </summary>
[TestClass]
public sealed class McpInputRequestTests
{
    private SocialAgentApp _app = null!;
    private readonly List<ElicitRequestParams> _asked = [];

    [TestInitialize]
    public async Task StartAsync()
    {
        _app = new SocialAgentApp();
        _app.Bluesky.SetNotifications(
            _app.Bluesky.Notification("old", "reply", createdAt: DateTimeOffset.UtcNow.AddDays(-10)),
            _app.Bluesky.Notification("new", "reply", createdAt: DateTimeOffset.UtcNow.AddHours(-1)));
        await _app.PollAsync();
    }

    [TestCleanup]
    public async Task StopAsync() => await _app.DisposeAsync();

    [TestMethod]
    public async Task An_unreadable_since_asks_once_and_uses_the_answer()
    {
        var answer = DateTimeOffset.UtcNow.AddDays(-2).ToString("O");
        await using var client = await ConnectAsync(Accept(answer));

        var result = await client.CallToolAsync("get_recent_mentions", new Dictionary<string, object?> { ["since"] = "last tuesday" });

        Assert.IsFalse(result.IsError ?? false, McpToolTests.Text(result));
        var question = _asked.Single();
        StringAssert.Contains(question.Message, "last tuesday");
        Assert.IsTrue(question.RequestedSchema!.Properties.ContainsKey("since"));
        Assert.IsEmpty(question.RequestedSchema.Required ?? [], "since is never required");
        Assert.AreEqual("new", Single(result));
    }

    [TestMethod]
    public async Task A_declined_question_falls_back_to_the_default_window_and_says_so()
    {
        await using var client = await ConnectAsync(Decline);

        var result = await client.CallToolAsync("get_recent_mentions", new Dictionary<string, object?> { ["since"] = "last tuesday" });

        Assert.HasCount(1, _asked);
        StringAssert.Contains(McpToolTests.Text(result).Split('\n')[0], "isn't an ISO-8601 timestamp");
        Assert.AreEqual(2, result.StructuredContent!.Value.GetProperty("mentions").GetArrayLength());
    }

    [TestMethod]
    public async Task A_client_without_mrtr_gets_the_default_with_a_notice()
    {
        await using var client = await ConnectAsync(Accept("unused"), protocolVersion: "2025-11-25");

        var result = await client.CallToolAsync("get_top_posts", new Dictionary<string, object?> { ["since"] = "yesterday-ish" });

        Assert.IsEmpty(_asked);
        Assert.IsFalse(result.IsError ?? false);
        StringAssert.Contains(result.StructuredContent!.Value.GetProperty("notice").GetString(), "the last 30 days");
    }

    [TestMethod]
    public async Task A_valid_since_asks_nothing()
    {
        await using var client = await ConnectAsync(Decline);

        await client.CallToolAsync("get_recent_mentions", new Dictionary<string, object?> { ["since"] = "2026-01-01T00:00:00Z" });

        Assert.IsEmpty(_asked);
    }

    private static string Single(CallToolResult result) =>
        result.StructuredContent!.Value.GetProperty("mentions").EnumerateArray().Single().GetProperty("platformNotificationId").GetString()!;

    private Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> Accept(string since) =>
        (request, _) =>
        {
            _asked.Add(request!);
            return ValueTask.FromResult(new ElicitResult
            {
                Action = "accept",
                Content = new Dictionary<string, JsonElement> { ["since"] = JsonSerializer.SerializeToElement(since) },
            });
        };

    private ValueTask<ElicitResult> Decline(ElicitRequestParams? request, CancellationToken _)
    {
        _asked.Add(request!);
        return ValueTask.FromResult(new ElicitResult { Action = "decline" });
    }

    private Task<McpClient> ConnectAsync(
        Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> handler, string? protocolVersion = null) =>
        _app.ConnectAsync(protocolVersion, new McpClientHandlers { ElicitationHandler = handler });
}
