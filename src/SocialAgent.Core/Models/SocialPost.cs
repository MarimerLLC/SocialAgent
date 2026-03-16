namespace SocialAgent.Core.Models;

public class SocialPost
{
    public required string Id { get; set; }
    public required string ProviderId { get; set; }
    public required string PlatformPostId { get; set; }
    public required string AuthorHandle { get; set; }
    public required string Content { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public string? InReplyToId { get; set; }
    public string? Url { get; set; }
    public int LikeCount { get; set; }
    public int RepostCount { get; set; }
    public int ReplyCount { get; set; }
    public bool IsOwnPost { get; set; }
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;
}
