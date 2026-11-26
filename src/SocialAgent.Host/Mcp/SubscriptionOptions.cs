namespace SocialAgent.Host.Mcp;

/// <summary>The <c>SocialAgent:Mcp:Subscriptions</c> section: limits on <c>subscriptions/listen</c> streams.</summary>
internal sealed class SubscriptionOptions
{
    public const string SectionName = "SocialAgent:Mcp:Subscriptions";

    /// <summary>
    /// How long one listen stream stays open before the server ends it and the client reconnects.
    /// Ending streams, rather than sending keepalives, keeps proxies from cutting an idle one.
    /// </summary>
    public double StreamMinutes { get; set; } = 25;

    /// <summary>
    /// Open listen streams per caller that watch at least one resource. Every API-key caller is the
    /// same identity, so with one key this is effectively a global limit.
    /// </summary>
    public int MaxStreamsPerCaller { get; set; } = 5;

    /// <summary>
    /// Open listen streams per caller that watch nothing (a client that asked only for
    /// list_changed, as Claude Code does). They do no work but each holds a request open.
    /// </summary>
    public int MaxIdleStreamsPerCaller { get; set; } = 20;

    /// <summary>Resource URIs one listen stream may ask for.</summary>
    public int MaxUrisPerStream { get; set; } = 50;
}
