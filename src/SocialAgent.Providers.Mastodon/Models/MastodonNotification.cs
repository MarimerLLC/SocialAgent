namespace SocialAgent.Providers.Mastodon;

internal class MastodonNotification
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public MastodonAccount? Account { get; set; }
    public MastodonStatus? Status { get; set; }
}
