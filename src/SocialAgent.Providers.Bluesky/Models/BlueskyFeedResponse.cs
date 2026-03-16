namespace SocialAgent.Providers.Bluesky;

internal class BlueskyFeedResponse
{
    public List<BlueskyFeedItem> Feed { get; set; } = [];
    public string? Cursor { get; set; }
}

internal class BlueskyFeedItem
{
    public BlueskyPostView Post { get; set; } = new();
}

internal class BlueskyPostView
{
    public string Uri { get; set; } = string.Empty;
    public string Cid { get; set; } = string.Empty;
    public BlueskyAuthor? Author { get; set; }
    public BlueskyRecord? Record { get; set; }
    public int LikeCount { get; set; }
    public int RepostCount { get; set; }
    public int ReplyCount { get; set; }
    public DateTimeOffset IndexedAt { get; set; }
}

internal class BlueskyAuthor
{
    public string Did { get; set; } = string.Empty;
    public string Handle { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Avatar { get; set; }
}

internal class BlueskyRecord
{
    public string? Text { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
}
