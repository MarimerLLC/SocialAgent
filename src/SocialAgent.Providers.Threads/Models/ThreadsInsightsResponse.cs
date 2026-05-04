namespace SocialAgent.Providers.Threads;

internal class ThreadsInsightsResponse
{
    public List<ThreadsInsightMetric>? Data { get; set; }
}

internal class ThreadsInsightMetric
{
    public string Name { get; set; } = string.Empty;
    public List<ThreadsInsightValue>? Values { get; set; }
    public ThreadsInsightTotalValue? TotalValue { get; set; }
}

internal class ThreadsInsightValue
{
    public int Value { get; set; }
}

internal class ThreadsInsightTotalValue
{
    public int Value { get; set; }
}
