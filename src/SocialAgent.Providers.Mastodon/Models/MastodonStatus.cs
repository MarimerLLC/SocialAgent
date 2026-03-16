namespace SocialAgent.Providers.Mastodon;

internal class MastodonStatus
{
    public string Id { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string? InReplyToId { get; set; }
    public string? Url { get; set; }
    public int FavouritesCount { get; set; }
    public int ReblogsCount { get; set; }
    public int RepliesCount { get; set; }
    public MastodonAccount? Account { get; set; }
}
