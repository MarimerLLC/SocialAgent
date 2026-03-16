namespace SocialAgent.Core.Models;

public class SocialProfile
{
    public required string ProviderId { get; set; }
    public required string Handle { get; set; }
    public string? DisplayName { get; set; }
    public string? Bio { get; set; }
    public string? AvatarUrl { get; set; }
    public int FollowerCount { get; set; }
    public int FollowingCount { get; set; }
    public int PostCount { get; set; }
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;
}
