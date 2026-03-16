namespace SocialAgent.Providers.Mastodon;

internal class MastodonAccount
{
    public string Id { get; set; } = string.Empty;
    public string Acct { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string Avatar { get; set; } = string.Empty;
    public int FollowersCount { get; set; }
    public int FollowingCount { get; set; }
    public int StatusesCount { get; set; }
}
