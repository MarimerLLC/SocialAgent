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
        _analytics.GetRecentMentionsAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _analytics.GetTopEngagersAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _analytics.GetPlatformComparisonAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _repository.GetUnreadNotificationsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _repository.GetStoredProviderIdsAsync(Arg.Any<CancellationToken>())
            .Returns(["mastodon", "bluesky"]);

        var services = new ServiceCollection();
        services.AddSingleton(_analytics);
        services.AddSingleton(_repository);
        // The enabled providers, as in production: providerId is resolved against these.
        services.AddSingleton(StubProvider("mastodon", "Mastodon"));
        services.AddSingleton(StubProvider("bluesky", "Bluesky"));
        _services = services.BuildServiceProvider();

        _dispatcher = new SkillDispatcher(
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SkillDispatcher>.Instance);
    }

    [TestCleanup]
    public void Cleanup() => _services.Dispose();

    private static Dictionary<string, JsonElement> Metadata(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static ISocialMediaProvider StubProvider(string id, string name)
    {
        var provider = Substitute.For<ISocialMediaProvider>();
        provider.ProviderId.Returns(id);
        provider.ProviderName.Returns(name);
        return provider;
    }

    [TestMethod]
    public async Task ProviderId_IsMatchedCaseInsensitively_BeforeQuerying()
    {
        await _dispatcher.DispatchAsync("recent-mentions", string.Empty, Metadata("""{"providerId":"Mastodon"}"""), CancellationToken.None);

        await _analytics.Received(1).GetRecentMentionsAsync(20, "mastodon", null, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ProviderIdListNamingEveryProvider_QueriesTheUnion()
    {
        // The combined request RockBot learned to avoid: it used to match nothing and return [].
        await _dispatcher.DispatchAsync("recent-mentions", string.Empty, Metadata("""{"providerId":"mastodon,bluesky"}"""), CancellationToken.None);

        await _analytics.Received(1).GetRecentMentionsAsync(20, null, null, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task ProviderIdAll_QueriesTheUnion()
    {
        await _dispatcher.DispatchAsync("engagement-summary", string.Empty, Metadata("""{"providerId":"all"}"""), CancellationToken.None);

        await _analytics.Received(1).GetEngagementSummaryAsync(null, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    // A rejected request answers {"error": ..., "validProviderIds": [...]}, never data.
    private static (string Error, string[] ValidProviderIds) ParseError(string reply)
    {
        using var json = JsonDocument.Parse(reply);
        Assert.AreEqual(JsonValueKind.Object, json.RootElement.ValueKind, $"expected an error object, got: {reply}");
        return (json.RootElement.GetProperty("error").GetString()!,
            json.RootElement.GetProperty("validProviderIds").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [TestMethod]
    public async Task UnknownProviderId_ReturnsAJsonError_WithoutQuerying()
    {
        var mentions = await _dispatcher.DispatchAsync("recent-mentions", string.Empty, Metadata("""{"providerId":"twitter"}"""), CancellationToken.None);

        var (error, valid) = ParseError(mentions);
        StringAssert.Contains(error, "Unknown providerId 'twitter'", "the message stays readable, not \\u0027-escaped");
        CollectionAssert.AreEqual(new[] { "mastodon", "bluesky" }, valid);
        await _analytics.DidNotReceiveWithAnyArgs().GetRecentMentionsAsync(default, default, default, default);

        // check-notifications goes to the repository rather than analytics; it is guarded too.
        var unread = await _dispatcher.DispatchAsync("check-notifications", string.Empty, Metadata("""{"providerId":"twitter"}"""), CancellationToken.None);

        StringAssert.Contains(ParseError(unread).Error, "Unknown providerId 'twitter'");
        await _repository.DidNotReceiveWithAnyArgs().GetUnreadNotificationsAsync(default, default);
    }

    [TestMethod]
    [DataRow("""{"providerId":42}""")]
    [DataRow("""{"providerId":true}""")]
    [DataRow("""{"providerId":{"id":"mastodon"}}""")]
    [DataRow("""{"providerId":["mastodon",7]}""")]
    public async Task NonStringProviderId_IsAnError_NotAWiderQuery(string metadata)
    {
        // Reading these as absent would silently query every provider.
        var result = await _dispatcher.DispatchAsync("recent-mentions", string.Empty, Metadata(metadata), CancellationToken.None);

        StringAssert.Contains(ParseError(result).Error, "providerId must be a string");
        await _analytics.DidNotReceiveWithAnyArgs().GetRecentMentionsAsync(default, default, default, default);
    }

    [TestMethod]
    public async Task ProviderIdArrayOfStrings_IsAccepted()
    {
        await _dispatcher.DispatchAsync("recent-mentions", string.Empty, Metadata("""{"providerId":["Bluesky"]}"""), CancellationToken.None);
        await _analytics.Received(1).GetRecentMentionsAsync(20, "bluesky", null, Arg.Any<CancellationToken>());

        await _dispatcher.DispatchAsync("recent-mentions", string.Empty, Metadata("""{"providerId":["mastodon","bluesky"]}"""), CancellationToken.None);
        await _analytics.Received(1).GetRecentMentionsAsync(20, null, null, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task NullProviderId_MeansEveryProvider()
    {
        await _dispatcher.DispatchAsync("recent-mentions", string.Empty, Metadata("""{"providerId":null}"""), CancellationToken.None);

        await _analytics.Received(1).GetRecentMentionsAsync(20, null, null, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task DisabledProviderWithStoredHistory_IsQueryable_AndNotSwallowedByAList()
    {
        // Threads is not enabled, but its rows are still stored.
        _repository.GetStoredProviderIdsAsync(Arg.Any<CancellationToken>()).Returns(["mastodon", "bluesky", "threads"]);

        await _dispatcher.DispatchAsync("engagement-summary", string.Empty, Metadata("""{"providerId":"threads"}"""), CancellationToken.None);
        await _analytics.Received(1).GetEngagementSummaryAsync("threads", Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());

        // Naming only the enabled two must not widen to null, which would include Threads.
        var subset = await _dispatcher.DispatchAsync("recent-mentions", string.Empty, Metadata("""{"providerId":"mastodon,bluesky"}"""), CancellationToken.None);
        var (error, valid) = ParseError(subset);
        StringAssert.Contains(error, "one provider or all of them");
        CollectionAssert.AreEqual(new[] { "mastodon", "bluesky", "threads" }, valid);
        await _analytics.DidNotReceiveWithAnyArgs().GetRecentMentionsAsync(default, default, default, default);
    }

    [TestMethod]
    public async Task SkillsWithoutProviderId_IgnoreIt()
    {
        // platform-comparison is all-platforms by design, so a bogus providerId must not block it.
        var result = await _dispatcher.DispatchAsync("platform-comparison", string.Empty, Metadata("""{"providerId":"twitter"}"""), CancellationToken.None);

        Assert.IsFalse(result.Contains("Unknown providerId", StringComparison.Ordinal));
        await _analytics.Received(1).GetPlatformComparisonAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public void AgentCard_DescribesProviderIdForEverySkillThatTakesIt()
    {
        foreach (var skill in SkillCatalog.AgentCardSkills)
        {
            var mentionsProviderId = skill.Description.Contains("providerId (string; one provider", StringComparison.Ordinal);
            Assert.AreEqual(SkillCatalog.TakesProviderId(skill.Id), mentionsProviderId,
                $"{skill.Id}: the card must describe providerId exactly when the skill filters by it");
        }
    }

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
        // count and since fall back to their defaults. providerId is the exception: a malformed one
        // is an error (see NonStringProviderId_IsAnError_NotAWiderQuery), because ignoring it
        // would widen the query to every provider.
        var metadata = Metadata("""{"count":"not-a-number","since":"never"}""");

        var result = await _dispatcher.DispatchAsync("top-posts", string.Empty, metadata, CancellationToken.None);

        Assert.IsNotNull(result);
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
