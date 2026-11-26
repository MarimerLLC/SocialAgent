namespace SocialAgent.Host.Services;

/// <summary>What a <see cref="SocialChange"/> is about.</summary>
internal enum SocialChangeKind
{
    /// <summary>A provider's poll started failing, or started working again.</summary>
    Providers,

    /// <summary>New mentions or replies were stored for <see cref="SocialChange.ProviderId"/>.</summary>
    Mentions,

    /// <summary>The set of unread notifications changed: one arrived, or one was read.</summary>
    Unread,

    /// <summary>The engagement counts on one of your posts moved.</summary>
    Post,
}

/// <summary>One change the polling loop saved, as the MCP listen streams hear about it.</summary>
/// <param name="ProviderId">The provider, for <see cref="SocialChangeKind.Mentions"/>, <see cref="SocialChangeKind.Post"/> and <see cref="SocialChangeKind.Providers"/>.</param>
/// <param name="PostId">The platform post id, for <see cref="SocialChangeKind.Post"/>.</param>
internal sealed record SocialChange(SocialChangeKind Kind, string? ProviderId = null, string? PostId = null);

/// <summary>
/// Tells open MCP listen streams that the polling loop saved something. In-process only: with
/// more than one replica this would need a backplane, so the deployment runs a single replica.
/// </summary>
internal interface ISocialChanges
{
    /// <summary>Raised by the polling loop after a change is saved.</summary>
    void Publish(SocialChange change);

    /// <summary>Calls <paramref name="handler"/> for every change until the result is disposed.</summary>
    IDisposable Subscribe(Action<SocialChange> handler);
}

/// <summary>The in-process <see cref="ISocialChanges"/>. A failing handler doesn't stop the others or the publisher.</summary>
internal sealed class SocialChanges : ISocialChanges
{
    private readonly Lock _lock = new();
    private Action<SocialChange>[] _handlers = [];

    public void Publish(SocialChange change)
    {
        foreach (var handler in Volatile.Read(ref _handlers))
        {
            try
            {
                handler(change);
            }
            catch
            {
                // A stream that went away mid-update; the change itself is already saved.
            }
        }
    }

    public IDisposable Subscribe(Action<SocialChange> handler)
    {
        lock (_lock)
        {
            _handlers = [.. _handlers, handler];
        }
        return new Subscription(this, handler);
    }

    private void Unsubscribe(Action<SocialChange> handler)
    {
        lock (_lock)
        {
            _handlers = [.. _handlers.Where(h => h != handler)];
        }
    }

    private sealed class Subscription(SocialChanges owner, Action<SocialChange> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unsubscribe(handler);
            }
        }
    }
}
