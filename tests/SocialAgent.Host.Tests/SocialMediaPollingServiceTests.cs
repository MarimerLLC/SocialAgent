using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SocialAgent.Core.Models;
using SocialAgent.Core.Providers;
using SocialAgent.Data.Repositories;
using SocialAgent.Host.Services;

namespace SocialAgent.Host.Tests;

[TestClass]
public class SocialMediaPollingServiceTests
{
    private static ServiceProvider BuildServices(params ISocialMediaProvider[] providers)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<ISocialDataRepository>());
        foreach (var provider in providers)
        {
            services.AddSingleton(provider);
        }
        return services.BuildServiceProvider();
    }

    private static IConfiguration FastPolling() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SocialAgent:PollingIntervalMinutes"] = "60"
            })
            .Build();

    private static ISocialMediaProvider Provider(string id, Exception? throws = null)
    {
        var provider = Substitute.For<ISocialMediaProvider>();
        provider.ProviderId.Returns(id);
        provider.ProviderName.Returns(id);
        if (throws is null)
        {
            provider.GetProfileAsync(Arg.Any<CancellationToken>())
                .Returns(new SocialProfile { ProviderId = id, Handle = id });
            provider.GetRecentPostsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns([]);
            provider.GetNotificationsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns([]);
        }
        else
        {
            provider.GetProfileAsync(Arg.Any<CancellationToken>()).ThrowsAsync(throws);
        }
        return provider;
    }

    /// <summary>
    /// A request timeout surfaces as TaskCanceledException, which is an OperationCanceledException.
    /// Filtering the handler on that type let a transient network blip escape ExecuteAsync, and the
    /// host's default StopHost behaviour then terminated the pod — the cause of repeated restarts
    /// in production. The service must absorb it and keep polling.
    /// </summary>
    [TestMethod]
    public async Task RequestTimeout_DoesNotTerminateTheService()
    {
        var provider = Provider("mastodon", new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.",
            new TimeoutException()));
        await using var services = BuildServices(provider);
        var service = new SocialMediaPollingService(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SocialMediaPollingService>.Instance,
            FastPolling(),
            new SocialChanges());

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        // Long enough to clear the service's 10s startup delay and run one cycle.
        await Task.Delay(TimeSpan.FromSeconds(13), CancellationToken.None);

        var executing = service.ExecuteTask;
        Assert.IsNotNull(executing);
        Assert.AreNotEqual(TaskStatus.Faulted, executing.Status,
            "a request timeout must not fault the background service");
        Assert.IsFalse(executing.IsCompleted, "the service should still be polling");

        await service.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task ProviderFailure_DoesNotStopOtherProviders()
    {
        var failing = Provider("mastodon", new HttpRequestException("boom"));
        var healthy = Provider("bluesky");
        await using var services = BuildServices(failing, healthy);
        var service = new SocialMediaPollingService(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SocialMediaPollingService>.Instance,
            FastPolling(),
            new SocialChanges());

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(13), CancellationToken.None);

        await healthy.Received().GetProfileAsync(Arg.Any<CancellationToken>());
        Assert.AreNotEqual(TaskStatus.Faulted, service.ExecuteTask!.Status);

        await service.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task Shutdown_StopsCleanly()
    {
        await using var services = BuildServices(Provider("mastodon"));
        var service = new SocialMediaPollingService(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SocialMediaPollingService>.Instance,
            FastPolling(),
            new SocialChanges());

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        var executing = service.ExecuteTask;
        Assert.IsNotNull(executing);
        Assert.AreNotEqual(TaskStatus.Faulted, executing.Status,
            "a normal shutdown must not surface as a fault");
    }

    // --- Change notifications (MCP push) --------------------------------------------------

    private static SocialNotification Notification(string id, string type, bool isRead = false) => new()
    {
        Id = $"bluesky:{id}",
        ProviderId = "bluesky",
        PlatformNotificationId = id,
        Type = type,
        FromHandle = "alice",
        CreatedAt = DateTimeOffset.UtcNow,
        IsRead = isRead
    };

    private static (SocialMediaPollingService Service, List<SocialChange> Published, ServiceProvider Services) PollingWith(
        ISocialDataRepository repository, ISocialMediaProvider provider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(repository);
        services.AddSingleton(provider);
        var sp = services.BuildServiceProvider();
        var changes = new SocialChanges();
        var published = new List<SocialChange>();
        changes.Subscribe(published.Add);
        var service = new SocialMediaPollingService(
            sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<SocialMediaPollingService>.Instance, FastPolling(), changes);
        return (service, published, sp);
    }

    [TestMethod]
    public async Task Poll_AnnouncesNewReplies_AsMentionsAndUnread()
    {
        var repository = Substitute.For<ISocialDataRepository>();
        var reply = Notification("r1", "reply");
        repository.UpsertNotificationsAsync(Arg.Any<IEnumerable<SocialNotification>>(), Arg.Any<CancellationToken>())
            .Returns(new NotificationUpsertResult([reply], 0));
        var provider = Provider("bluesky");
        provider.GetNotificationsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns([reply]);
        var (service, published, sp) = PollingWith(repository, provider);
        await using var _ = sp;

        await service.PollOnceAsync(CancellationToken.None);

        CollectionAssert.AreEquivalent(
            new[] { new SocialChange(SocialChangeKind.Mentions, "bluesky"), new SocialChange(SocialChangeKind.Unread, "bluesky") },
            published);
    }

    [TestMethod]
    public async Task Poll_AnnouncesALike_AsUnreadOnly_AndNothingForAlreadyStoredRows()
    {
        var repository = Substitute.For<ISocialDataRepository>();
        var like = Notification("l1", "like");
        repository.UpsertNotificationsAsync(Arg.Any<IEnumerable<SocialNotification>>(), Arg.Any<CancellationToken>())
            .Returns(new NotificationUpsertResult([like], 0), new NotificationUpsertResult([], 0));
        var provider = Provider("bluesky");
        provider.GetNotificationsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns([like]);
        var (service, published, sp) = PollingWith(repository, provider);
        await using var _ = sp;

        await service.PollOnceAsync(CancellationToken.None);
        await service.PollOnceAsync(CancellationToken.None);

        Assert.AreEqual(new SocialChange(SocialChangeKind.Unread, "bluesky"), published.Single());
    }

    [TestMethod]
    public async Task Poll_AnnouncesEachOwnPostWhoseEngagementMoved()
    {
        var repository = Substitute.For<ISocialDataRepository>();
        var post = new SocialPost
        {
            Id = "bluesky:p", ProviderId = "bluesky", PlatformPostId = "at://me/app.bsky.feed.post/p",
            AuthorHandle = "me", Content = "hi", CreatedAt = DateTimeOffset.UtcNow, LikeCount = 2, IsOwnPost = true
        };
        repository.UpsertPostsAsync(Arg.Any<IEnumerable<SocialPost>>(), Arg.Any<CancellationToken>())
            .Returns(new PostUpsertResult(0, [post]));
        var provider = Provider("bluesky");
        provider.GetRecentPostsAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns([post]);
        var (service, published, sp) = PollingWith(repository, provider);
        await using var _ = sp;

        await service.PollOnceAsync(CancellationToken.None);

        Assert.AreEqual(new SocialChange(SocialChangeKind.Post, "bluesky", post.PlatformPostId), published.Single());
    }

    [TestMethod]
    public async Task Poll_AnnouncesAProviderOnlyWhenItsOutcomeFlips()
    {
        var provider = Provider("mastodon");
        var calls = 0;
        provider.GetProfileAsync(Arg.Any<CancellationToken>()).Returns(_ => ++calls == 2
            ? throw new HttpRequestException("down")
            : Task.FromResult(new SocialProfile { ProviderId = "mastodon", Handle = "me" }));
        var (service, published, sp) = PollingWith(Substitute.For<ISocialDataRepository>(), provider);
        await using var _ = sp;

        await service.PollOnceAsync(CancellationToken.None);   // ok: first outcome, nothing to compare
        await service.PollOnceAsync(CancellationToken.None);   // fails: flip
        await service.PollOnceAsync(CancellationToken.None);   // ok: flip back
        await service.PollOnceAsync(CancellationToken.None);   // ok: no change

        Assert.AreEqual(2, published.Count);
        Assert.IsTrue(published.All(c => c == new SocialChange(SocialChangeKind.Providers, "mastodon")));
    }

    // --- Engagement refresh window ---------------------------------------------------------

    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void PostsSince_FirstEverPoll_PassesNullThrough()
    {
        Assert.IsNull(SocialMediaPollingService.PostsSince(null, refreshDays: 7, Now),
            "a first poll must keep the providers' bounded backfill behaviour");
    }

    [TestMethod]
    public void PostsSince_RecentPoll_ReachesBackAcrossTheWindow()
    {
        // Fetching only since the last poll froze each post's engagement at its first few minutes.
        var since = SocialMediaPollingService.PostsSince(Now.AddMinutes(-5), refreshDays: 7, Now);

        Assert.AreEqual(Now.AddDays(-7), since);
    }

    [TestMethod]
    public void PostsSince_PollOlderThanWindow_UsesTheLastPoll()
    {
        // After an outage longer than the window, the gap must still be covered.
        var lastPoll = Now.AddDays(-12);

        Assert.AreEqual(lastPoll, SocialMediaPollingService.PostsSince(lastPoll, refreshDays: 7, Now));
    }

    [TestMethod]
    public void PostsSince_ZeroWindow_FallsBackToIncrementalFetching()
    {
        // EngagementRefreshDays = 0 turns the refresh off: back to fetching only since the last poll.
        var lastPoll = Now.AddMinutes(-5);

        Assert.AreEqual(lastPoll, SocialMediaPollingService.PostsSince(lastPoll, refreshDays: 0, Now));
    }

    [TestMethod]
    public async Task Poll_RefetchesPostsAcrossWindow_ButNotificationsOnlySinceLastPoll()
    {
        var lastPoll = DateTimeOffset.UtcNow.AddMinutes(-5);
        var repository = Substitute.For<ISocialDataRepository>();
        repository.GetPollStateAsync("mastodon", Arg.Any<CancellationToken>())
            .Returns(new PollState { ProviderId = "mastodon", LastPollTime = lastPoll });

        var provider = Provider("mastodon");
        var services = new ServiceCollection();
        services.AddSingleton(repository);
        services.AddSingleton(provider);
        await using var sp = services.BuildServiceProvider();

        var service = new SocialMediaPollingService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SocialMediaPollingService>.Instance,
            FastPolling(),
            new SocialChanges());
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(13), CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        await provider.Received().GetRecentPostsAsync(
            Arg.Is<DateTimeOffset?>(d => d.HasValue && d.Value < DateTimeOffset.UtcNow.AddDays(-6.9)),
            Arg.Any<CancellationToken>());
        await provider.Received().GetNotificationsAsync(lastPoll, Arg.Any<CancellationToken>());
    }
}
