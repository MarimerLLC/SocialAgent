namespace SocialAgent.Core.Models;

public class EngagementSummary
{
    public required string ProviderId { get; set; }
    public required DateTimeOffset PeriodStart { get; set; }
    public required DateTimeOffset PeriodEnd { get; set; }
    public int TotalPosts { get; set; }
    public int TotalLikes { get; set; }
    public int TotalReposts { get; set; }
    public int TotalReplies { get; set; }
    public int TotalMentions { get; set; }
    public int NewFollowers { get; set; }
    public double AvgLikesPerPost { get; set; }
    public double AvgRepostsPerPost { get; set; }
    public double AvgRepliesPerPost { get; set; }
    public List<TopEngager> TopEngagers { get; set; } = [];
}

public class TopEngager
{
    public required string Handle { get; set; }
    public int InteractionCount { get; set; }
    public string? MostCommonInteractionType { get; set; }
}
