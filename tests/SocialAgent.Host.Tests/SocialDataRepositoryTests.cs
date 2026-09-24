using Microsoft.EntityFrameworkCore;
using SocialAgent.Core.Models;
using SocialAgent.Data;
using SocialAgent.Data.Repositories;

namespace SocialAgent.Host.Tests;

[TestClass]
public class SocialDataRepositoryTests
{
    private string _dbPath = null!;
    private SocialAgentDbContext _db = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"socialagent-repo-{Guid.NewGuid():N}.db");
        _db = new SocialAgentDbContext(new DbContextOptionsBuilder<SocialAgentDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options);
        await _db.Database.EnsureCreatedAsync();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _db.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private static SocialPost Post(string id, int likes, int reposts = 0, int replies = 0) => new()
    {
        Id = $"mastodon:{id}",
        ProviderId = "mastodon",
        PlatformPostId = id,
        AuthorHandle = "rockylhotka",
        Content = "hello",
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
        LikeCount = likes,
        RepostCount = reposts,
        ReplyCount = replies,
        IsOwnPost = true
    };

    [TestMethod]
    public async Task UpsertPosts_ReturnsCountOfNewPostsOnly()
    {
        var repository = new SocialDataRepository(_db);

        Assert.AreEqual(2, await repository.UpsertPostsAsync([Post("1", 0), Post("2", 0)]));
        Assert.AreEqual(1, await repository.UpsertPostsAsync([Post("1", 0), Post("2", 0), Post("3", 0)]),
            "re-fetched posts are refreshes, not new posts");
        Assert.AreEqual(0, await repository.UpsertPostsAsync([Post("1", 0)]));
    }

    [TestMethod]
    public async Task UpsertPosts_RefreshesEngagementOnPostsAlreadyStored()
    {
        // The production symptom: a post stored with 0 likes that later picked up a like, and the
        // stored count never moved because the post was only ever fetched once.
        var repository = new SocialDataRepository(_db);
        await repository.UpsertPostsAsync([Post("117311409989332474", likes: 0, reposts: 1)]);
        var firstSeen = (await _db.Posts.AsNoTracking().SingleAsync()).LastUpdated;

        await Task.Delay(20);
        await repository.UpsertPostsAsync([Post("117311409989332474", likes: 1, reposts: 1, replies: 2)]);

        var stored = await _db.Posts.AsNoTracking().SingleAsync();
        Assert.AreEqual(1, stored.LikeCount);
        Assert.AreEqual(1, stored.RepostCount);
        Assert.AreEqual(2, stored.ReplyCount);
        Assert.IsTrue(stored.LastUpdated > firstSeen, "a refresh should move LastUpdated");
    }
}
