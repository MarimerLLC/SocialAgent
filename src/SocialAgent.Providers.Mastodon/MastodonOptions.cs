namespace SocialAgent.Providers.Mastodon;

public class MastodonOptions
{
    public bool Enabled { get; set; }
    public string InstanceUrl { get; set; } = "https://mastodon.social";
    public string AccessToken { get; set; } = string.Empty;
}
