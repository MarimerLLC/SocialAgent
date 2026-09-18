using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SocialAgent.Core.Analytics;
using SocialAgent.Core.Models;
using SocialAgent.Core.Providers;
using SocialAgent.Data.Repositories;
using SocialAgent.Host;

namespace SocialAgent.Host.Tests;

[TestClass]
public class SkillCatalogTests
{
    [TestMethod]
    public void AgentCardSkills_AndRouterDefinitions_CoverTheSameSkills()
    {
        var cardIds = SkillCatalog.AgentCardSkills.Select(s => s.Id).OrderBy(s => s, StringComparer.Ordinal);
        var routerIds = SkillCatalog.RouterDefinitions.Select(s => s.Id).OrderBy(s => s, StringComparer.Ordinal);

        CollectionAssert.AreEqual(cardIds.ToList(), routerIds.ToList(),
            "the agent card and the router must advertise the same skills");
    }

    [TestMethod]
    public void EveryAdvertisedSkill_IsKnown()
    {
        foreach (var skill in SkillCatalog.AgentCardSkills)
        {
            Assert.IsTrue(SkillCatalog.IsKnownSkill(skill.Id), $"{skill.Id} should be dispatchable");
        }
    }

    [TestMethod]
    public void IsKnownSkill_RejectsUnknownIds()
    {
        Assert.IsFalse(SkillCatalog.IsKnownSkill("not-a-skill"));
        Assert.IsFalse(SkillCatalog.IsKnownSkill("Engagement-Summary"), "skill ids are case sensitive");
    }

    [TestMethod]
    [DataRow("show me my engagement summary", "engagement-summary")]
    [DataRow("what are my TOP POSTS?", "top-posts")]
    [DataRow("any recent mentions", "recent-mentions")]
    [DataRow("who are my top engagers", "follower-insights")]
    [DataRow("compare platforms please", "platform-comparison")]
    [DataRow("check notifications", "check-notifications")]
    [DataRow("provider status", "provider-status")]
    public void MatchSkillByKeywords_MapsNaturalLanguage(string text, string expected)
    {
        Assert.AreEqual(expected, SkillCatalog.MatchSkillByKeywords(text));
    }

    [TestMethod]
    public void MatchSkillByKeywords_UnmatchedText_IsNotAKnownSkill()
    {
        var result = SkillCatalog.MatchSkillByKeywords("tell me a joke");

        Assert.IsFalse(SkillCatalog.IsKnownSkill(result),
            "unmatched text must fall through to the dispatcher's unknown-skill branch");
    }
}

[TestClass]
public class SkillDispatcherTests
{
    private IAnalyticsService _analytics = null!;
    private ISocialDataRepository _repository = null!;
    private ServiceProvider _services = null!;
    private SkillDispatcher _dispatcher = null!;

    [TestInitialize]
    public void Setup()
    {
        _analytics = Substitute.For<IAnalyticsService>();
        _repository = Substitute.For<ISocialDataRepository>();

        _analytics.GetEngagementSummaryAsync(Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new EngagementSummary
            {
                ProviderId = "all",
                PeriodStart = DateTimeOffset.UtcNow.AddDays(-7),
                PeriodEnd = DateTimeOffset.UtcNow
            });
        _analytics.GetTopPostsAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _analytics.GetRecentMentionsAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _analytics.GetTopEngagersAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _analytics.GetPlatformComparisonAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _repository.GetUnreadNotificationsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var services = new ServiceCollection();
        services.AddSingleton(_analytics);
        services.AddSingleton(_repository);
        _services = services.BuildServiceProvider();

        _dispatcher = new SkillDispatcher(
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SkillDispatcher>.Instance);
    }

    [TestCleanup]
    public void Cleanup() => _services.Dispose();

    private static Dictionary<string, JsonElement> Metadata(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [TestMethod]
    public async Task ExplicitSkillId_TakesPrecedenceOverText()
    {
        var result = await _dispatcher.DispatchAsync("top-posts", "check notifications", null, CancellationToken.None);

        await _analytics.Received(1).GetTopPostsAsync(
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
        Assert.IsNotNull(result);
    }

    [TestMethod]
    public async Task UnknownExplicitSkillId_FallsBackToKeywordMatching()
    {
        await _dispatcher.DispatchAsync("bogus-skill", "check notifications", null, CancellationToken.None);

        await _repository.Received(1).GetUnreadNotificationsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task UnrecognisedText_ReturnsSkillListing()
    {
        var result = await _dispatcher.DispatchAsync(null, "tell me a joke", null, CancellationToken.None);

        StringAssert.Contains(result, "Unknown skill");
        StringAssert.Contains(result, "engagement-summary");
    }

    [TestMethod]
    public async Task CountParameter_IsPassedThrough_AsNumberOrString()
    {
        await _dispatcher.DispatchAsync("top-posts", string.Empty, Metadata("""{"count":5}"""), CancellationToken.None);
        await _analytics.Received(1).GetTopPostsAsync(5, Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());

        await _dispatcher.DispatchAsync("top-posts", string.Empty, Metadata("""{"count":"7"}"""), CancellationToken.None);
        await _analytics.Received(1).GetTopPostsAsync(7, Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ProviderIdAndSince_ArePassedThrough()
    {
        var metadata = Metadata("""{"providerId":"mastodon","since":"2026-01-01T00:00:00Z"}""");

        await _dispatcher.DispatchAsync("engagement-summary", string.Empty, metadata, CancellationToken.None);

        await _analytics.Received(1).GetEngagementSummaryAsync(
            "mastodon",
            Arg.Is<DateTimeOffset?>(d => d == new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task MalformedParameters_AreIgnored_RatherThanThrowing()
    {
        var metadata = Metadata("""{"count":"not-a-number","since":"never","providerId":42}""");

        var result = await _dispatcher.DispatchAsync("top-posts", string.Empty, metadata, CancellationToken.None);

        Assert.IsNotNull(result);
        // Defaults survive: count falls back to 10, providerId and since to null.
        await _analytics.Received(1).GetTopPostsAsync(10, null, null, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ProviderStatus_ReportsEachProvider()
    {
        var provider = Substitute.For<ISocialMediaProvider>();
        provider.ProviderId.Returns("mastodon");
        provider.ProviderName.Returns("Mastodon");
        provider.ValidateConnectionAsync(Arg.Any<CancellationToken>()).Returns(true);

        var services = new ServiceCollection();
        services.AddSingleton(_analytics);
        services.AddSingleton(_repository);
        services.AddSingleton(provider);
        using var sp = services.BuildServiceProvider();
        var dispatcher = new SkillDispatcher(
            sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<SkillDispatcher>.Instance);

        var result = await dispatcher.DispatchAsync("provider-status", string.Empty, null, CancellationToken.None);

        StringAssert.Contains(result, "mastodon");
        StringAssert.Contains(result, "true");
    }
}
