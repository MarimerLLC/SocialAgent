using NSubstitute;
using SocialAgent.Core.Models;
using SocialAgent.Data.Repositories;

namespace SocialAgent.Analytics.Tests;

[TestClass]
public class AnalyticsServiceTests
{
    private ISocialDataRepository _repository = null!;
    private AnalyticsService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _repository = Substitute.For<ISocialDataRepository>();
        _service = new AnalyticsService(_repository);

        // Default every aggregate to empty; individual tests override what they care about.
        _repository.GetPostEngagementTotalsAsync(Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(PostEngagementTotals.Empty);
        _repository.GetNotificationCountsByTypeAsync(Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int>());
        _repository.GetEngagerTalliesAsync(Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([]);
    }

    [TestMethod]
    public async Task GetEngagementSummary_WithPosts_CalculatesAverages()
    {
        _repository.GetPostEngagementTotalsAsync(Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new PostEngagementTotals(PostCount: 2, Likes: 30, Reposts: 6, Replies: 9));

        var summary = await _service.GetEngagementSummaryAsync();

        Assert.AreEqual(2, summary.TotalPosts);
        Assert.AreEqual(30, summary.TotalLikes);
        Assert.AreEqual(6, summary.TotalReposts);
        Assert.AreEqual(9, summary.TotalReplies);
        Assert.AreEqual(15.0, summary.AvgLikesPerPost);
        Assert.AreEqual(3.0, summary.AvgRepostsPerPost);
        Assert.AreEqual(4.5, summary.AvgRepliesPerPost);
    }

    [TestMethod]
    public async Task GetEngagementSummary_WithNoData_ReturnsZeros()
    {
        var summary = await _service.GetEngagementSummaryAsync();

        Assert.AreEqual(0, summary.TotalPosts);
        Assert.AreEqual(0.0, summary.AvgLikesPerPost);
        Assert.AreEqual(0, summary.TotalMentions);
        Assert.AreEqual(0, summary.NewFollowers);
    }

    [TestMethod]
    public async Task GetEngagementSummary_CountsMentionsAndFollows()
    {
        _repository.GetNotificationCountsByTypeAsync(Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int> { ["mention"] = 4, ["follow"] = 2, ["like"] = 11 });

        var summary = await _service.GetEngagementSummaryAsync();

        Assert.AreEqual(4, summary.TotalMentions);
        Assert.AreEqual(2, summary.NewFollowers);
    }

    [TestMethod]
    public async Task GetEngagementSummary_DefaultsToLastSevenDays()
    {
        await _service.GetEngagementSummaryAsync();

        await _repository.Received(1).GetPostEngagementTotalsAsync(
            Arg.Any<string?>(),
            Arg.Is<DateTimeOffset?>(d => d.HasValue
                && (DateTimeOffset.UtcNow - d.Value).TotalDays > 6.9
                && (DateTimeOffset.UtcNow - d.Value).TotalDays < 7.1),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task GetTopEngagers_GroupsByHandle()
    {
        _repository.GetEngagerTalliesAsync(Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([
                new EngagerTally("alice", "like", 2),
                new EngagerTally("alice", "mention", 1),
                new EngagerTally("bob", "repost", 1)
            ]);

        var engagers = await _service.GetTopEngagersAsync();

        Assert.AreEqual(2, engagers.Count);
        Assert.AreEqual("alice", engagers[0].Handle);
        Assert.AreEqual(3, engagers[0].InteractionCount);
        Assert.AreEqual("like", engagers[0].MostCommonInteractionType);
        Assert.AreEqual("bob", engagers[1].Handle);
        Assert.AreEqual(1, engagers[1].InteractionCount);
    }

    [TestMethod]
    public async Task GetTopEngagers_RespectsCount()
    {
        _repository.GetEngagerTalliesAsync(Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([
                new EngagerTally("alice", "like", 5),
                new EngagerTally("bob", "like", 4),
                new EngagerTally("carol", "like", 3)
            ]);

        var engagers = await _service.GetTopEngagersAsync(count: 2);

        Assert.AreEqual(2, engagers.Count);
        Assert.AreEqual("alice", engagers[0].Handle);
        Assert.AreEqual("bob", engagers[1].Handle);
    }

    [TestMethod]
    public async Task GetPlatformComparison_SummarisesEachProfile()
    {
        _repository.GetProfilesAsync(Arg.Any<CancellationToken>()).Returns([
            new SocialProfile { ProviderId = "mastodon", Handle = "me@m.test" },
            new SocialProfile { ProviderId = "bluesky", Handle = "me.bsky.social" }
        ]);

        var comparison = await _service.GetPlatformComparisonAsync();

        Assert.AreEqual(2, comparison.Count);
        Assert.AreEqual("mastodon", comparison[0].ProviderId);
        Assert.AreEqual("bluesky", comparison[1].ProviderId);
    }
}
