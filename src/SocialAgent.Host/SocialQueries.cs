using SocialAgent.Core.Analytics;
using SocialAgent.Core.Models;
using SocialAgent.Core.Providers;
using SocialAgent.Data.Repositories;

namespace SocialAgent.Host;

/// <summary>One provider's connection check, as provider-status and get_provider_status report it.</summary>
internal sealed record ProviderStatus(string ProviderId, string ProviderName, bool Connected);

/// <summary>
/// The queries every front end runs (A2A skills, MCP tools and resources), so they resolve
/// <c>providerId</c> the same way and read the same data. Each front end only shapes the output.
/// Scoped: it holds the scope's repository.
/// </summary>
internal sealed class SocialQueries(
    IEnumerable<ISocialMediaProvider> providers,
    ISocialDataRepository repository,
    IAnalyticsService analytics)
{
    /// <summary>The enabled providers plus any with stored rows (<see cref="ProviderSelection.KnownProviderIds"/>).</summary>
    public async Task<IReadOnlyList<string>> KnownProviderIdsAsync(CancellationToken ct) =>
        ProviderSelection.KnownProviderIds(
            providers.Select(p => p.ProviderId),
            await repository.GetStoredProviderIdsAsync(ct));

    /// <summary>
    /// Resolves a caller's <c>providerId</c> (see <see cref="ProviderSelection.Resolve"/>), and
    /// returns the known ids too so a rejection can list them.
    /// </summary>
    public async Task<(ProviderSelectionResult Selection, IReadOnlyList<string> KnownProviderIds)> ResolveProviderAsync(
        string? requested, CancellationToken ct)
    {
        var known = await KnownProviderIdsAsync(ct);
        return (ProviderSelection.Resolve(requested, known), known);
    }

    // `provider` below is a resolved filter: null for every provider, or one canonical id.

    public Task<EngagementSummary> GetEngagementSummaryAsync(string? provider, DateTimeOffset? since, CancellationToken ct) =>
        analytics.GetEngagementSummaryAsync(provider, since, ct);

    public Task<IReadOnlyList<SocialPost>> GetTopPostsAsync(int count, string? provider, DateTimeOffset? since, CancellationToken ct) =>
        analytics.GetTopPostsAsync(count, provider, since, ct);

    public Task<IReadOnlyList<SocialNotification>> GetRecentMentionsAsync(int count, string? provider, DateTimeOffset? since, CancellationToken ct) =>
        analytics.GetRecentMentionsAsync(count, provider, since, ct);

    public Task<IReadOnlyList<TopEngager>> GetTopEngagersAsync(int count, string? provider, DateTimeOffset? since, CancellationToken ct) =>
        analytics.GetTopEngagersAsync(count, provider, since, ct);

    public Task<IReadOnlyList<EngagementSummary>> GetPlatformComparisonAsync(DateTimeOffset? since, CancellationToken ct) =>
        analytics.GetPlatformComparisonAsync(since, ct);

    public Task<IReadOnlyList<SocialNotification>> GetUnreadNotificationsAsync(string? provider, CancellationToken ct) =>
        repository.GetUnreadNotificationsAsync(provider, ct);

    /// <summary>Checks each enabled provider's connection, in registration order.</summary>
    public async Task<IReadOnlyList<ProviderStatus>> GetProviderStatusesAsync(CancellationToken ct)
    {
        var statuses = new List<ProviderStatus>();
        foreach (var provider in providers)
        {
            statuses.Add(new ProviderStatus(provider.ProviderId, provider.ProviderName, await provider.ValidateConnectionAsync(ct)));
        }
        return statuses;
    }
}
