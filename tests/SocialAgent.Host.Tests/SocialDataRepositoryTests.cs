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

    private static SocialPost Post(string id, int likes, int reposts = 0, int replies = 0, DateTimeOffset? createdAt = null) => new()
    {
        Id = $"mastodon:{id}",
        ProviderId = "mastodon",
        PlatformPostId = id,
        AuthorHandle = "rockylhotka",
        Content = "hello",
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow.AddDays(-2),
        LikeCount = likes,
        RepostCount = reposts,
        ReplyCount = replies,
        IsOwnPost = true
    };

    private static SocialNotification Notification(string id, DateTimeOffset createdAt, string type = "mention",
        string from = "someone", bool isRead = false) => new()
    {
        Id = $"mastodon:{id}",
        ProviderId = "mastodon",
        PlatformNotificationId = id,
        Type = type,
        FromHandle = from,
        CreatedAt = createdAt,
        IsRead = isRead
    };

    // Fixed instants, so ordering and cut-offs don't depend on when the test runs.
    private static readonly DateTimeOffset Day1 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2 = Day1.AddDays(1);
    private static readonly DateTimeOffset Day3 = Day1.AddDays(2);

    // The queries below are the ones issue #8 found broken on SQLite: EF could not translate a
    // DateTimeOffset comparison or ORDER BY there until the column was stored as a binary long.

    [TestMethod]
    public async Task GetPosts_FiltersBySince_NewestFirst_AndHonoursLimit()
    {
        var repository = new SocialDataRepository(_db);
        await repository.UpsertPostsAsync([Post("a", 0, createdAt: Day1), Post("c", 0, createdAt: Day3), Post("b", 0, createdAt: Day2)]);

        var all = await repository.GetPostsAsync();
        CollectionAssert.AreEqual(new[] { "c", "b", "a" }, all.Select(p => p.PlatformPostId).ToArray());

        var sinceDay2 = await repository.GetPostsAsync(since: Day2);
        CollectionAssert.AreEqual(new[] { "c", "b" }, sinceDay2.Select(p => p.PlatformPostId).ToArray(), "since is inclusive");

        var newest = await repository.GetPostsAsync(limit: 1);
        Assert.AreEqual("c", newest.Single().PlatformPostId);
    }

    [TestMethod]
    public async Task GetTopPostsByEngagement_FiltersBySince()
    {
        var repository = new SocialDataRepository(_db);
        await repository.UpsertPostsAsync([Post("old-popular", likes: 50, createdAt: Day1), Post("new", likes: 3, createdAt: Day3)]);

        var top = await repository.GetTopPostsByEngagementAsync(10, since: Day2);

        Assert.AreEqual("new", top.Single().PlatformPostId);
    }

    [TestMethod]
    public async Task GetNotifications_FiltersBySince_NewestFirst()
    {
        var repository = new SocialDataRepository(_db);
        await repository.UpsertNotificationsAsync([Notification("1", Day1), Notification("3", Day3), Notification("2", Day2)]);

        var sinceDay2 = await repository.GetNotificationsAsync(since: Day2);

        CollectionAssert.AreEqual(new[] { "3", "2" }, sinceDay2.Select(n => n.PlatformNotificationId).ToArray());
    }

    [TestMethod]
    public async Task GetUnreadNotifications_NewestFirst()
    {
        var repository = new SocialDataRepository(_db);
        await repository.UpsertNotificationsAsync([
            Notification("1", Day1), Notification("3", Day3), Notification("2", Day2, isRead: true)]);

        var unread = await repository.GetUnreadNotificationsAsync();

        CollectionAssert.AreEqual(new[] { "3", "1" }, unread.Select(n => n.PlatformNotificationId).ToArray());
    }

    [TestMethod]
    public async Task Aggregates_FilterBySince()
    {
        var repository = new SocialDataRepository(_db);
        await repository.UpsertPostsAsync([Post("old", likes: 10, createdAt: Day1), Post("new", likes: 2, reposts: 1, createdAt: Day3)]);
        await repository.UpsertNotificationsAsync([
            Notification("1", Day1, type: "like", from: "alice"),
            Notification("2", Day3, type: "mention", from: "bob"),
            Notification("3", Day3, type: "mention", from: "bob")]);

        var totals = await repository.GetPostEngagementTotalsAsync(since: Day2);
        Assert.AreEqual(new PostEngagementTotals(1, 2, 1, 0), totals);

        var counts = await repository.GetNotificationCountsByTypeAsync(since: Day2);
        Assert.AreEqual(1, counts.Count);
        Assert.AreEqual(2, counts["mention"]);

        var tallies = await repository.GetEngagerTalliesAsync(since: Day2);
        Assert.AreEqual(new EngagerTally("bob", "mention", 2), tallies.Single());
    }

    [TestMethod]
    public async Task PurgeOld_DeletesOnlyRowsOlderThanTheCutoff()
    {
        var repository = new SocialDataRepository(_db);
        await repository.UpsertPostsAsync([Post("old", 0, createdAt: Day1), Post("new", 0, createdAt: Day3)]);
        await repository.UpsertNotificationsAsync([Notification("old", Day1), Notification("new", Day3)]);

        Assert.AreEqual(1, await repository.PurgeOldPostsAsync(Day2));
        Assert.AreEqual(1, await repository.PurgeOldNotificationsAsync(Day2));

        Assert.AreEqual("new", (await _db.Posts.AsNoTracking().SingleAsync()).PlatformPostId);
        Assert.AreEqual("new", (await _db.Notifications.AsNoTracking().SingleAsync()).PlatformNotificationId);
    }

    [TestMethod]
    public async Task GetStoredProviderIds_ListsProvidersWithPostsOrNotifications_Once()
    {
        var repository = new SocialDataRepository(_db);
        Assert.AreEqual(0, (await repository.GetStoredProviderIdsAsync()).Count);

        await repository.UpsertPostsAsync([Post("1", 0), Post("2", 0)]);   // mastodon
        var bluesky = Notification("n1", Day1);
        bluesky.Id = "bluesky:n1";
        bluesky.ProviderId = "bluesky";
        await repository.UpsertNotificationsAsync([bluesky, Notification("n2", Day2)]);   // bluesky, mastodon
        // A profile alone doesn't count: profiles are never purged.
        await repository.UpsertProfileAsync(new SocialProfile { ProviderId = "threads", Handle = "me" });

        var stored = await repository.GetStoredProviderIdsAsync();

        CollectionAssert.AreEquivalent(new[] { "mastodon", "bluesky" }, stored.ToArray());
    }

    [TestMethod]
    public async Task DateTimeOffset_KeepsItsInstant_AndReadsBackAsUtc()
    {
        var repository = new SocialDataRepository(_db);
        var india = new DateTimeOffset(2026, 11, 25, 21, 15, 30, 123, TimeSpan.FromHours(5.5)).AddTicks(4567);
        await repository.UpsertPostsAsync([Post("india", 0, createdAt: india)]);

        var stored = (await _db.Posts.AsNoTracking().SingleAsync()).CreatedAt;

        Assert.AreEqual(india.UtcTicks, stored.UtcTicks, "the instant survives to the tick");
        Assert.AreEqual(TimeSpan.Zero, stored.Offset, "values read back as UTC, as PostgreSQL's timestamptz returns them");
    }

    [TestMethod]
    public async Task Ordering_AndSince_FollowTheInstant_AcrossOffsets()
    {
        // India's wall clock (21:00) is later than Chicago's (10:00), but the Chicago post happened
        // half an hour later. Ordering by local clock ticks, as EF's DateTimeOffsetToBinaryConverter
        // stores them, would get this backwards.
        var repository = new SocialDataRepository(_db);
        var india = new DateTimeOffset(2026, 11, 25, 21, 0, 0, TimeSpan.FromHours(5.5));   // 15:30 UTC
        var chicago = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.FromHours(-6));  // 16:00 UTC
        await repository.UpsertPostsAsync([Post("india", 0, createdAt: india), Post("chicago", 0, createdAt: chicago)]);

        var newestFirst = await repository.GetPostsAsync();
        CollectionAssert.AreEqual(new[] { "chicago", "india" }, newestFirst.Select(p => p.PlatformPostId).ToArray());

        var since = await repository.GetPostsAsync(since: new DateTimeOffset(2026, 11, 25, 15, 45, 0, TimeSpan.Zero));
        Assert.AreEqual("chicago", since.Single().PlatformPostId);
    }

    [TestMethod]
    public async Task UpsertPosts_ReturnsCountOfNewPostsOnly()
    {
        var repository = new SocialDataRepository(_db);

        Assert.AreEqual(2, (await repository.UpsertPostsAsync([Post("1", 0), Post("2", 0)])).Inserted);
        Assert.AreEqual(1, (await repository.UpsertPostsAsync([Post("1", 0), Post("2", 0), Post("3", 0)])).Inserted,
            "re-fetched posts are refreshes, not new posts");
        Assert.AreEqual(0, (await repository.UpsertPostsAsync([Post("1", 0)])).Inserted);
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

    [TestMethod]
    public async Task UpsertPosts_ReportsOnlyPostsWhoseEngagementMoved()
    {
        var repository = new SocialDataRepository(_db);
        var first = await repository.UpsertPostsAsync([Post("1", likes: 0), Post("2", likes: 3)]);
        Assert.AreEqual(0, first.EngagementChanged.Count, "new posts are inserted, not changed");

        var refresh = await repository.UpsertPostsAsync([Post("1", likes: 1), Post("2", likes: 3), Post("3", likes: 0)]);

        Assert.AreEqual(1, refresh.Inserted);
        Assert.AreEqual("1", refresh.EngagementChanged.Single().PlatformPostId);
        Assert.AreEqual(1, refresh.EngagementChanged.Single().LikeCount);

        var unchanged = await repository.UpsertPostsAsync([Post("1", likes: 1), Post("2", likes: 3)]);
        Assert.AreEqual(0, unchanged.EngagementChanged.Count, "a refresh with the same counts changes nothing");
    }

    [TestMethod]
    public async Task UpsertNotifications_ReturnsOnlyNewRows_AndCountsReadStateChanges()
    {
        var repository = new SocialDataRepository(_db);
        var first = await repository.UpsertNotificationsAsync([Notification("1", Day1), Notification("2", Day2)]);
        CollectionAssert.AreEqual(new[] { "1", "2" }, first.Inserted.Select(n => n.PlatformNotificationId).ToArray());
        Assert.AreEqual(0, first.ReadStateChanged);

        var second = await repository.UpsertNotificationsAsync([
            Notification("1", Day1, isRead: true), Notification("2", Day2), Notification("3", Day3)]);

        Assert.AreEqual("3", second.Inserted.Single().PlatformNotificationId);
        Assert.AreEqual(1, second.InsertedCount);
        Assert.AreEqual(1, second.ReadStateChanged, "only notification 1 went from unread to read");
    }

    [TestMethod]
    public async Task UpsertNotifications_BackfillsAMissingUrl_ButKeepsAStoredOne()
    {
        var repository = new SocialDataRepository(_db);
        await repository.UpsertNotificationsAsync([Notification("1", Day1), WithUrl(Notification("2", Day1), "https://x.test/old")]);

        await repository.UpsertNotificationsAsync([
            WithUrl(Notification("1", Day1), "https://x.test/1"), WithUrl(Notification("2", Day1), "https://x.test/new")]);

        var stored = await _db.Notifications.AsNoTracking().OrderBy(n => n.PlatformNotificationId).ToListAsync();
        Assert.AreEqual("https://x.test/1", stored[0].Url);
        Assert.AreEqual("https://x.test/old", stored[1].Url);
    }

    [TestMethod]
    public async Task GetNotifications_FiltersByTypes()
    {
        var repository = new SocialDataRepository(_db);
        await repository.UpsertNotificationsAsync([
            Notification("1", Day1, type: "mention"), Notification("2", Day2, type: "reply"),
            Notification("3", Day3, type: "like")]);

        var mentions = await repository.GetNotificationsAsync(types: NotificationTypes.Mentions);

        CollectionAssert.AreEqual(new[] { "2", "1" }, mentions.Select(n => n.PlatformNotificationId).ToArray());
    }

    [TestMethod]
    public async Task GetPost_FindsByProviderAndPlatformId()
    {
        var repository = new SocialDataRepository(_db);
        await repository.UpsertPostsAsync([Post("1", likes: 4)]);

        Assert.AreEqual(4, (await repository.GetPostAsync("mastodon", "1"))!.LikeCount);
        Assert.IsNull(await repository.GetPostAsync("bluesky", "1"));
        Assert.IsNull(await repository.GetPostAsync("mastodon", "2"));
    }

    private static SocialNotification WithUrl(SocialNotification notification, string url)
    {
        notification.Url = url;
        return notification;
    }
}
