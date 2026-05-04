namespace SocialAgent.Providers.Threads;

internal class ThreadsRefreshResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string TokenType { get; set; } = string.Empty;
    public long ExpiresIn { get; set; }
}
