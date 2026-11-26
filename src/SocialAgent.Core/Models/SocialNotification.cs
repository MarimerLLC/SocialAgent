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

    /// <summary>
    /// A web link to the post the notification is about (the mention or reply itself), when the
    /// platform gives one. Null for notifications with no post, such as a follow.
    /// </summary>
    public string? Url { get; set; }

    public bool IsRead { get; set; }
}

/// <summary>The normalized <see cref="SocialNotification.Type"/> values that callers group on.</summary>
public static class NotificationTypes
{
    public const string Mention = "mention";
    public const string Reply = "reply";

    /// <summary>
    /// What "mentions" means everywhere (skills, tools, resources): someone mentioning you, or
    /// replying to you. Bluesky and Threads report a reply as its own type, so filtering on
    /// <see cref="Mention"/> alone hid every reply.
    /// </summary>
    public static IReadOnlyList<string> Mentions { get; } = [Mention, Reply];

    public static bool IsMention(string type) => type is Mention or Reply;
}
