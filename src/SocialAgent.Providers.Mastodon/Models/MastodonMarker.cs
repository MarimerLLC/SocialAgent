namespace SocialAgent.Providers.Mastodon;

internal class MastodonMarkersResponse
{
    public MastodonMarker? Notifications { get; set; }
}

internal class MastodonMarker
{
    public string LastReadId { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
