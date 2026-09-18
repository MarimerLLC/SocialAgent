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
            FastPolling());

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
            FastPolling());

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
            FastPolling());

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        var executing = service.ExecuteTask;
        Assert.IsNotNull(executing);
        Assert.AreNotEqual(TaskStatus.Faulted, executing.Status,
            "a normal shutdown must not surface as a fault");
    }
}
