using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SocialAgent.Host.Services;

namespace SocialAgent.Host.Mcp;

/// <summary>
/// The <c>subscriptions/listen</c> handler (SEP-2575, protocol 2026-07-28): pushes
/// <c>notifications/resources/updated</c> for <see cref="SocialResources"/> over the listen
/// request's own response stream, which is the only channel the stateless transport has. The
/// subscription is the open stream; nothing is stored. Changes come from the polling loop
/// (<see cref="ISocialChanges"/>), so updates arrive no faster than the poll interval.
/// </summary>
internal static class SocialSubscriptions
{
    public static async ValueTask<EmptyResult> ListenAsync(RequestContext<SubscriptionsListenRequestParams> request, CancellationToken cancellationToken)
    {
        var services = request.Services ?? throw new InvalidOperationException("The listen request has no services.");
        var options = services.GetRequiredService<IOptions<SubscriptionOptions>>().Value;
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SocialSubscriptions));
        // Every API-key caller is the same identity; in Development there is no sign-in at all.
        var caller = request.User?.Identity?.Name is { Length: > 0 } name ? name : "anonymous";

        var requested = (request.Params?.Notifications?.ResourceSubscriptions ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (requested.Count > options.MaxUrisPerStream)
        {
            throw new McpException($"A listen request can watch at most {options.MaxUrisPerStream} resources; this one asked for {requested.Count}.");
        }
        var resources = requested
            .Select(uri => (Uri: uri, Resource: SocialResourceUri.TryParse(uri)))
            .Where(r => r.Resource is not null)
            .ToList();

        // Every stream holds a slot, reserved before any reads: a watching slot if it names a
        // resource, else one of the larger pool of idle slots.
        var streams = services.GetRequiredService<ListenStreams>();
        var slot = Reserve(streams, caller, resources.Count > 0 ? StreamKind.Watching : StreamKind.Idle, options);
        try
        {
            // Subscribe before the first reads, so a change saved while they run isn't missed.
            var pending = new PendingChanges();
            using var subscription = services.GetRequiredService<ISocialChanges>().Subscribe(pending.Add);

            var scopes = services.GetRequiredService<IServiceScopeFactory>();
            var watches = new List<Watch>();
            foreach (var (uri, resource) in resources)
            {
                // Granted only if it resolves now (known provider, stored post); otherwise not acknowledged at all.
                if (await ReadAsync(scopes, resource!, cancellationToken) is { } snapshot)
                {
                    watches.Add(new Watch(uri, snapshot.Resolved) { Marker = snapshot.Marker });
                }
            }
            pending.WatchOnly(change => watches.Any(w => w.Resource.Affects(change)));
            if (watches.Count == 0 && slot.Kind == StreamKind.Watching)
            {
                // Nothing it named resolved, so it watches nothing after all.
                slot.Dispose();
                slot = Reserve(streams, caller, StreamKind.Idle, options);
            }

            var subscriptionId = JsonSerializer.SerializeToNode(request.JsonRpcRequest.Id, McpJsonUtilities.DefaultOptions);
            // Only resource subscriptions are honoured; list_changed requests are left out of the
            // acknowledgement. The SDK's acknowledgement type has no _meta, so the tag is added to its JSON.
            var ack = JsonSerializer.SerializeToNode(
                new SubscriptionsAcknowledgedNotificationParams
                {
                    Notifications = new SubscriptionsListenNotifications { ResourceSubscriptions = [.. watches.Select(w => w.Uri)] },
                },
                McpJsonUtilities.DefaultOptions)!.AsObject();
            ack["_meta"] = Tag(subscriptionId);
            await request.Server.SendNotificationAsync(
                NotificationMethods.SubscriptionsAcknowledgedNotification, ack, cancellationToken: cancellationToken);
            log.LogInformation("Listen stream opened with {Granted} of {Requested} resources", watches.Count, requested.Count);

            // Ending the stream after a while, rather than sending keepalives, keeps proxies from
            // cutting it while idle; the client reconnects.
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lifetime.CancelAfter(TimeSpan.FromMinutes(options.StreamMinutes));
            var reason = "failed";
            try
            {
                while (true)
                {
                    var (changes, everything) = await pending.TakeAsync(lifetime.Token);
                    var affected = watches
                        .Where(w => w.Active && (everything || changes.Any(w.Resource.Affects)))
                        .ToList();
                    foreach (var watch in affected)
                    {
                        var snapshot = await ReadAsync(scopes, watch.Resource, lifetime.Token);
                        if (snapshot is null)
                        {
                            // The post aged out, or the provider is gone: nothing more to say about it.
                            watch.Active = false;
                            continue;
                        }
                        if (snapshot.Marker == watch.Marker)
                        {
                            continue;
                        }
                        watch.Marker = snapshot.Marker;
                        await request.Server.SendNotificationAsync(
                            NotificationMethods.ResourceUpdatedNotification,
                            new ResourceUpdatedNotificationParams { Uri = watch.Uri, Meta = Tag(subscriptionId) },
                            cancellationToken: lifetime.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                reason = "lifetime ended";
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    reason = "client went away";
                }
                log.LogInformation("Listen stream closed ({Reason})", reason);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new EmptyResult();
        }
        finally
        {
            slot.Dispose();
        }
    }

    private static ListenStreams.Slot Reserve(ListenStreams streams, string caller, StreamKind kind, SubscriptionOptions options) =>
        kind == StreamKind.Watching
            ? streams.TryOpen(caller, kind, options.MaxStreamsPerCaller)
                ?? throw new McpException($"You already have {options.MaxStreamsPerCaller} listen streams open. Close one, or watch more resources on one stream.")
            : streams.TryOpen(caller, kind, options.MaxIdleStreamsPerCaller)
                ?? throw new McpException($"You already have {options.MaxIdleStreamsPerCaller} listen streams open that watch nothing. Close one first.");

    private static async Task<ResourceSnapshot?> ReadAsync(IServiceScopeFactory scopes, SocialResourceUri resource, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return (await SocialResourceReader.ReadAsync(resource, scope.ServiceProvider, cancellationToken)).Snapshot;
    }

    private static JsonObject Tag(JsonNode? subscriptionId) => new() { [MetaKeys.SubscriptionId] = subscriptionId?.DeepClone() };

    private sealed record Watch(string Uri, SocialResourceUri Resource)
    {
        public string? Marker { get; set; }
        public bool Active { get; set; } = true;
    }
}

/// <summary>
/// The changes since the stream last looked, each kept once however often it happened, so a busy
/// post can't push out a mention. Before the watched resources are known (while the first reads
/// run) every change is kept, up to <see cref="Limit"/>; past that, everything watched is treated
/// as changed.
/// </summary>
internal sealed class PendingChanges
{
    public const int Limit = 256;

    private readonly Lock _lock = new();
    private readonly HashSet<SocialChange> _changed = [];
    private Func<SocialChange, bool>? _watched;
    private bool _overflowed;

    // Holds at most one wake-up: TakeAsync takes everything pending at once.
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public void Add(SocialChange change)
    {
        lock (_lock)
        {
            if (_watched is not null && !_watched(change))
            {
                return;
            }
            if (_changed.Count < Limit)
            {
                _changed.Add(change);
            }
            else
            {
                _overflowed = true;
            }
        }
        _wake.Writer.TryWrite(true);
    }

    /// <summary>From now on only changes this accepts count; earlier changes it rejects are dropped.</summary>
    public void WatchOnly(Func<SocialChange, bool> watched)
    {
        lock (_lock)
        {
            _watched = watched;
            _changed.RemoveWhere(c => !watched(c));
        }
    }

    /// <summary>
    /// Waits for at least one change, then returns every pending one and clears them.
    /// <c>Everything</c> is true when some were dropped for space, so every watch should re-read.
    /// </summary>
    public async Task<(IReadOnlyCollection<SocialChange> Changes, bool Everything)> TakeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _wake.Reader.ReadAsync(cancellationToken);
            lock (_lock)
            {
                var everything = _overflowed && _watched is not null;
                if (everything)
                {
                    _overflowed = false;
                }
                if (_changed.Count == 0 && !everything)
                {
                    continue;
                }
                var taken = _changed.ToList();
                _changed.Clear();
                return (taken, everything);
            }
        }
    }
}

internal enum StreamKind
{
    /// <summary>Watches at least one resource.</summary>
    Watching,

    /// <summary>Watches nothing; the client only holds it open.</summary>
    Idle,
}

/// <summary>
/// Counts each caller's open listen streams by <see cref="StreamKind"/>, so one caller can't hold
/// an unbounded number open.
/// </summary>
internal sealed class ListenStreams
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _open = [];

    /// <summary>Takes a slot for the caller; disposing it gives it back. Null when they already have <paramref name="max"/>.</summary>
    public Slot? TryOpen(string caller, StreamKind kind, int max)
    {
        var key = kind + ":" + caller;
        lock (_lock)
        {
            var count = _open.GetValueOrDefault(key);
            if (count >= max)
            {
                return null;
            }
            _open[key] = count + 1;
        }
        return new Slot(this, key, kind);
    }

    private void Close(string key)
    {
        lock (_lock)
        {
            if (_open.GetValueOrDefault(key) <= 1)
            {
                _open.Remove(key);
            }
            else
            {
                _open[key]--;
            }
        }
    }

    public sealed class Slot(ListenStreams owner, string key, StreamKind kind) : IDisposable
    {
        private int _disposed;

        public StreamKind Kind { get; } = kind;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Close(key);
            }
        }
    }
}
