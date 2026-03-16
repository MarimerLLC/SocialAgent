namespace SocialAgent.Core.Models;

/// <summary>
/// Tracks the last poll position for each provider to support incremental fetching.
/// </summary>
public class PollState
{
    public required string ProviderId { get; set; }
    public string? LastPostId { get; set; }
    public string? LastNotificationId { get; set; }
    public DateTimeOffset? LastPollTime { get; set; }
}
