namespace SocialAgent.Core.Models;

public class SocialNotification
{
    public required string Id { get; set; }
    public required string ProviderId { get; set; }
    public required string PlatformNotificationId { get; set; }
    public required string Type { get; set; }  // mention, like, repost, follow, reply
    public required string FromHandle { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public string? RelatedPostId { get; set; }
    public string? Content { get; set; }
    public bool IsRead { get; set; }
}
