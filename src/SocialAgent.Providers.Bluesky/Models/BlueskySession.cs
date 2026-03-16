namespace SocialAgent.Providers.Bluesky;

internal class BlueskySession
{
    public string AccessJwt { get; set; } = string.Empty;
    public string RefreshJwt { get; set; } = string.Empty;
    public string Did { get; set; } = string.Empty;
    public string Handle { get; set; } = string.Empty;
}
