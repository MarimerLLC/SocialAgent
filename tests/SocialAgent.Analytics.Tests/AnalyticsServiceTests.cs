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
    }

    [TestMethod]
    public async Task GetEngagementSummary_WithPosts_CalculatesAverages()
    {
        var posts = new List<SocialPost>
        {
            new()
            {
                Id = "test:1", ProviderId = "test", PlatformPostId = "1",
                AuthorHandle = "me", Content = "Post 1",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
                LikeCount = 10, RepostCount = 2, ReplyCount = 3, IsOwnPost = true
            },
            new()
            {
                Id = "test:2", ProviderId = "test", PlatformPostId = "2",
                AuthorHandle = "me", Content = "Post 2",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
                LikeCount = 20, RepostCount = 4, ReplyCount = 6, IsOwnPost = true
            }
        };

        _repository.GetPostsAsync(Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Is<bool?>(b => b == true), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(posts);
        _repository.GetNotificationsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SocialNotification>());

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
        _repository.GetPostsAsync(Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<bool?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SocialPost>());
        _repository.GetNotificationsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<SocialNotification>());

        var summary = await _service.GetEngagementSummaryAsync();

        Assert.AreEqual(0, summary.TotalPosts);
        Assert.AreEqual(0.0, summary.AvgLikesPerPost);
    }

    [TestMethod]
    public async Task GetTopEngagers_GroupsByHandle()
    {
        var notifications = new List<SocialNotification>
        {
            new() { Id = "1", ProviderId = "test", PlatformNotificationId = "1", Type = "like", FromHandle = "alice", CreatedAt = DateTimeOffset.UtcNow },
            new() { Id = "2", ProviderId = "test", PlatformNotificationId = "2", Type = "like", FromHandle = "alice", CreatedAt = DateTimeOffset.UtcNow },
            new() { Id = "3", ProviderId = "test", PlatformNotificationId = "3", Type = "mention", FromHandle = "alice", CreatedAt = DateTimeOffset.UtcNow },
            new() { Id = "4", ProviderId = "test", PlatformNotificationId = "4", Type = "repost", FromHandle = "bob", CreatedAt = DateTimeOffset.UtcNow },
        };

        _repository.GetNotificationsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(notifications);

        var engagers = await _service.GetTopEngagersAsync();

        Assert.AreEqual(2, engagers.Count);
        Assert.AreEqual("alice", engagers[0].Handle);
        Assert.AreEqual(3, engagers[0].InteractionCount);
        Assert.AreEqual("like", engagers[0].MostCommonInteractionType);
        Assert.AreEqual("bob", engagers[1].Handle);
        Assert.AreEqual(1, engagers[1].InteractionCount);
    }
}
