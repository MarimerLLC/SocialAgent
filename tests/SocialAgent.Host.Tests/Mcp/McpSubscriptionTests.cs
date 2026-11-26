using System.Threading.Channels;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SocialAgent.Host.Mcp;

namespace SocialAgent.Host.Tests.Mcp;

/// <summary>Push updates over <c>subscriptions/listen</c>, through the real host, the SDK client and a real poll.</summary>
[TestClass]
public sealed class McpSubscriptionTests
{
    private const string Mentions = "social://mentions";
    private const string BlueskyMentions = "social://mentions/bluesky";
    private const string MastodonMentions = "social://mentions/mastodon";
    private const string Unread = "social://notifications/unread";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private SocialAgentApp _app = null!;

    [TestInitialize]
    public void Start() => _app = new SocialAgentApp();

    [TestCleanup]
    public async Task StopAsync() => await _app.DisposeAsync();

    [TestMethod]
    public async Task A_new_bluesky_reply_is_pushed_to_the_mentions_and_unread_watchers_only()
    {
        await using var client = await _app.ConnectAsync();
        await using var listener = await Listener.OpenAsync(client, Mentions, BlueskyMentions, MastodonMentions, Unread);

        _app.Bluesky.SetNotifications(_app.Bluesky.Notification("r1", "reply"));
        await _app.PollAsync();
        var updates = await listener.ReceiveAsync(uris => uris.Count >= 3);

        CollectionAssert.AreEquivalent(new[] { Mentions, BlueskyMentions, MastodonMentions, Unread }, listener.Granted);
        CollectionAssert.AreEquivalent(new[] { Mentions, BlueskyMentions, Unread }, updates, "nothing for mastodon");
    }

    [TestMethod]
    public async Task Each_update_is_tagged_with_the_listen_request_id()
    {
        await using var client = await _app.ConnectAsync();
        await using var listener = await Listener.OpenAsync(client, BlueskyMentions);

        _app.Bluesky.SetNotifications(_app.Bluesky.Notification("r1", "reply"));
        await _app.PollAsync();
        var update = await listener.NextAsync();

        Assert.AreEqual(NotificationMethods.ResourceUpdatedNotification, update.Method);
        Assert.AreEqual(BlueskyMentions, Listener.UriOf(update));
        Assert.AreEqual(listener.Id, update.Params!["_meta"]![MetaKeys.SubscriptionId]!.GetValue<string>());
    }

    [TestMethod]
    public async Task A_new_like_is_pushed_only_to_unread()
    {
        await using var client = await _app.ConnectAsync();
        await using var listener = await Listener.OpenAsync(client, Mentions, BlueskyMentions, Unread);

        _app.Bluesky.SetNotifications(_app.Bluesky.Notification("l1", "like"));
        await _app.PollAsync();
        var updates = await listener.ReceiveAsync(uris => uris.Count >= 1);

        CollectionAssert.AreEqual(new[] { Unread }, updates);
    }

    [TestMethod]
    public async Task A_poll_that_stores_nothing_new_pushes_nothing()
    {
        _app.Bluesky.SetNotifications(_app.Bluesky.Notification("r1", "reply"));
        await _app.PollAsync();
        await using var client = await _app.ConnectAsync();
        await using var listener = await Listener.OpenAsync(client, Mentions, Unread);

        await _app.PollAsync();

        Assert.IsEmpty(await listener.ReceiveForAsync(TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    public async Task A_change_in_a_posts_engagement_is_pushed_to_that_post()
    {
        const string postId = "at://did:plc:me/app.bsky.feed.post/3abc";
        _app.Bluesky.SetPosts(_app.Bluesky.Post(postId, likes: 1), _app.Bluesky.Post("other", likes: 1));
        await _app.PollAsync();
        var uri = SocialResourceUri.Post("bluesky", postId);
        var other = SocialResourceUri.Post("bluesky", "other");
        await using var client = await _app.ConnectAsync();
        await using var listener = await Listener.OpenAsync(client, uri, other);

        _app.Bluesky.SetPosts(_app.Bluesky.Post(postId, likes: 2), _app.Bluesky.Post("other", likes: 1));
        await _app.PollAsync();
        var updates = await listener.ReceiveAsync(uris => uris.Count >= 1);

        CollectionAssert.AreEquivalent(new[] { uri, other }, listener.Granted);
        CollectionAssert.AreEqual(new[] { uri }, updates);
    }

    [TestMethod]
    public async Task A_provider_that_starts_failing_is_pushed_to_the_providers_watcher()
    {
        await _app.PollAsync();
        await using var client = await _app.ConnectAsync();
        await using var listener = await Listener.OpenAsync(client, "social://providers");

        _app.Mastodon.Connected = false;
        await _app.PollAsync();

        Assert.AreEqual("social://providers", Listener.UriOf(await listener.NextAsync()));
    }

    [TestMethod]
    public async Task Uris_that_do_not_resolve_are_not_acknowledged()
    {
        await using var client = await _app.ConnectAsync();

        await using var listener = await Listener.OpenAsync(client,
            "social://mentions/twitter", "social://posts/bluesky/nope", "https://example.com/elsewhere", "social://nothing", BlueskyMentions);

        CollectionAssert.AreEqual(new[] { BlueskyMentions }, listener.Granted);
    }

    [TestMethod]
    public async Task A_client_on_an_older_protocol_gets_method_not_found_and_tools_still_work()
    {
        await using var client = await _app.ConnectAsync(protocolVersion: "2025-11-25");

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => Listener.OpenAsync(client, Mentions));

        Assert.AreEqual(McpErrorCode.MethodNotFound, error.ErrorCode);
        Assert.IsFalse((await client.CallToolAsync("get_provider_status")).IsError ?? false);
    }

    [TestMethod]
    public async Task A_caller_can_hold_five_watching_streams_and_open_another_once_one_closes()
    {
        await using var client = await _app.ConnectAsync();
        var listeners = new List<Listener>();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                listeners.Add(await Listener.OpenAsync(client, Mentions));
            }

            var refused = await Assert.ThrowsAsync<McpProtocolException>(() => Listener.OpenAsync(client, Mentions));
            StringAssert.Contains(refused.Message, "5 listen streams");

            await listeners[0].DisposeAsync();
            listeners.Add(await RetryAsync(() => Listener.OpenAsync(client, Mentions)));
        }
        finally
        {
            foreach (var listener in listeners)
            {
                await listener.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task Streams_that_watch_nothing_have_their_own_limit()
    {
        await _app.DisposeAsync();
        _app = new SocialAgentApp(("SocialAgent:Mcp:Subscriptions:MaxIdleStreamsPerCaller", "2"));
        await using var client = await _app.ConnectAsync();
        var listeners = new List<Listener>();
        try
        {
            listeners.Add(await Listener.OpenAsync(client));
            listeners.Add(await Listener.OpenAsync(client, "social://mentions/twitter"));

            var refused = await Assert.ThrowsAsync<McpProtocolException>(() => Listener.OpenAsync(client));
            StringAssert.Contains(refused.Message, "2 listen streams open that watch nothing");
            listeners.Add(await Listener.OpenAsync(client, Mentions));
        }
        finally
        {
            foreach (var listener in listeners)
            {
                await listener.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task A_stream_may_watch_at_most_fifty_resources()
    {
        await using var client = await _app.ConnectAsync();
        var uris = Enumerable.Range(0, 51).Select(i => SocialResourceUri.Post("bluesky", $"post{i}")).ToArray();

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => Listener.OpenAsync(client, uris));

        StringAssert.Contains(error.Message, "at most 50");
    }

    [TestMethod]
    public async Task The_server_ends_a_stream_when_its_lifetime_is_up()
    {
        await _app.DisposeAsync();
        _app = new SocialAgentApp(("SocialAgent:Mcp:Subscriptions:StreamMinutes", "0.02"));
        await using var client = await _app.ConnectAsync();

        await using var watching = await Listener.OpenAsync(client, Mentions);
        await using var empty = await Listener.OpenAsync(client);

        Assert.IsNotNull(await watching.Completion.WaitAsync(Patience));
        Assert.IsNotNull(await empty.Completion.WaitAsync(Patience));
    }

    /// <summary>Retries while the server still counts a stream the client just closed.</summary>
    private static async Task<T> RetryAsync<T>(Func<Task<T>> action)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            try
            {
                return await action();
            }
            catch (McpProtocolException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }
    }

    /// <summary>
    /// One <c>subscriptions/listen</c> request, held open. The SDK client has no helper for it, so
    /// this sends the raw request and collects the notifications tagged with its id.
    /// </summary>
    private sealed class Listener : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancel = new();
        private readonly Channel<JsonRpcNotification> _updates = Channel.CreateUnbounded<JsonRpcNotification>();
        private readonly TaskCompletionSource<string[]> _ack = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<IAsyncDisposable> _handlers = [];

        private Listener() { }

        public string Id { get; } = "listen-" + Guid.NewGuid().ToString("N");

        public string[] Granted { get; private set; } = [];

        public Task<EmptyResult> Completion { get; private set; } = null!;

        public static string UriOf(JsonRpcNotification notification) => notification.Params!["uri"]!.GetValue<string>();

        public static async Task<Listener> OpenAsync(McpClient client, params string[] uris)
        {
            var listener = new Listener();
            listener._handlers.Add(client.RegisterNotificationHandler(NotificationMethods.SubscriptionsAcknowledgedNotification, (n, _) =>
            {
                if (listener.IsMine(n))
                {
                    var granted = n.Params?["notifications"]?["resourceSubscriptions"]?.AsArray().Select(u => u!.GetValue<string>()).ToArray() ?? [];
                    listener._ack.TrySetResult(granted);
                }
                return ValueTask.CompletedTask;
            }));
            listener._handlers.Add(client.RegisterNotificationHandler(NotificationMethods.ResourceUpdatedNotification, (n, _) =>
            {
                if (listener.IsMine(n))
                {
                    listener._updates.Writer.TryWrite(n);
                }
                return ValueTask.CompletedTask;
            }));

            listener.Completion = client.SendRequestAsync<SubscriptionsListenRequestParams, EmptyResult>(
                RequestMethods.SubscriptionsListen,
                new SubscriptionsListenRequestParams { Notifications = new SubscriptionsListenNotifications { ResourceSubscriptions = uris } },
                requestId: new RequestId(listener.Id),
                cancellationToken: listener._cancel.Token).AsTask();

            var first = await Task.WhenAny(listener._ack.Task, listener.Completion).WaitAsync(Patience);
            if (first == listener.Completion && !listener._ack.Task.IsCompleted)
            {
                await listener.DisposeAsync();
                await listener.Completion; // throws the server's error
                Assert.Fail("The stream ended without an acknowledgement.");
            }
            listener.Granted = await listener._ack.Task;
            return listener;
        }

        public async Task<JsonRpcNotification> NextAsync() => await _updates.Reader.ReadAsync().AsTask().WaitAsync(Patience);

        /// <summary>
        /// The URIs of the updates that arrive until <paramref name="done"/> says enough, plus any
        /// that follow within half a second, so an extra notification is caught.
        /// </summary>
        public async Task<List<string>> ReceiveAsync(Func<List<string>, bool> done)
        {
            var uris = new List<string>();
            while (!done(uris))
            {
                uris.Add(UriOf(await NextAsync()));
            }
            uris.AddRange(await ReceiveForAsync(TimeSpan.FromMilliseconds(500)));
            return uris;
        }

        /// <summary>The URIs of the updates that arrive within <paramref name="window"/>.</summary>
        public async Task<List<string>> ReceiveForAsync(TimeSpan window)
        {
            var uris = new List<string>();
            using var settle = new CancellationTokenSource(window);
            try
            {
                while (true)
                {
                    uris.Add(UriOf(await _updates.Reader.ReadAsync(settle.Token)));
                }
            }
            catch (OperationCanceledException)
            {
            }
            return uris;
        }

        private bool IsMine(JsonRpcNotification notification) =>
            notification.Params?["_meta"]?[MetaKeys.SubscriptionId]?.GetValue<string>() == Id;

        public async ValueTask DisposeAsync()
        {
            await _cancel.CancelAsync();
            foreach (var handler in _handlers)
            {
                await handler.DisposeAsync();
            }
            _handlers.Clear();
            try
            {
                await Completion;
            }
            catch (Exception)
            {
                // Cancelled, or the error OpenAsync already reported.
            }
        }
    }
}
