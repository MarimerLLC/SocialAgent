namespace SocialAgent.Providers.Threads;

internal class ThreadsConversationItem
{
    public string Id { get; set; } = string.Empty;
    public string? Text { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? Permalink { get; set; }
    public string? Username { get; set; }
    public int RepliesCount { get; set; }
    public int RepostsCount { get; set; }
    public int QuotesCount { get; set; }
    public string? MediaType { get; set; }
    public string? MediaUrl { get; set; }
    public bool IsQuotePost { get; set; }
}
