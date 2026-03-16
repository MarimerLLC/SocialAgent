namespace SocialAgent.Providers.Bluesky;

public class BlueskyOptions
{
    public bool Enabled { get; set; }
    public string ServiceUrl { get; set; } = "https://bsky.social";
    public string Handle { get; set; } = string.Empty;
    public string AppPassword { get; set; } = string.Empty;
}
