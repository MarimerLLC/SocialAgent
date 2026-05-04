namespace SocialAgent.Providers.Threads;

public class ThreadsOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://graph.threads.net";
    public string AccessToken { get; set; } = string.Empty;

    public bool IncludePostInsights { get; set; }

    public int RefreshThresholdDays { get; set; } = 7;
    public int RefreshCheckIntervalHours { get; set; } = 24;
}
