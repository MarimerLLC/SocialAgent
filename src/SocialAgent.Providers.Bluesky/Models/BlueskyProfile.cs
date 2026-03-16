namespace SocialAgent.Providers.Bluesky;

internal class BlueskyProfile
{
    public string Did { get; set; } = string.Empty;
    public string Handle { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public string? Avatar { get; set; }
    public int FollowersCount { get; set; }
    public int FollowsCount { get; set; }
    public int PostsCount { get; set; }
}
