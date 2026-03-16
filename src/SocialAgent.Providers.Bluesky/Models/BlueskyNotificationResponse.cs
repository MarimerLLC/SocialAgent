namespace SocialAgent.Providers.Bluesky;

internal class BlueskyNotificationResponse
{
    public List<BlueskyNotificationItem> Notifications { get; set; } = [];
    public string? Cursor { get; set; }
}

internal class BlueskyNotificationItem
{
    public string Uri { get; set; } = string.Empty;
    public string Cid { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public BlueskyAuthor? Author { get; set; }
    public BlueskyRecord? Record { get; set; }
    public DateTimeOffset IndexedAt { get; set; }
    public bool IsRead { get; set; }
}
