using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SocialAgent.Core.Providers;
using SocialAgent.Data.Repositories;
using SocialAgent.Host.Services;

namespace SocialAgent.Host.Mcp;

/// <summary>A provider in <c>social://providers</c>: its last poll's outcome and time, null before the first poll.</summary>
internal sealed record ProviderPollStatus(string ProviderId, string ProviderName, bool? LastPollSucceeded, DateTimeOffset? LastPolledAt);

/// <summary>A resource as read now: its JSON, the marker inside it, and the resolved URI.</summary>
internal sealed record ResourceSnapshot(string Json, string Marker, SocialResourceUri Resolved);

/// <summary>A read's outcome: the snapshot, or why the resource can't be read.</summary>
internal readonly record struct ResourceRead(ResourceSnapshot? Snapshot, string? Error);

/// <summary>
/// Reads the <c>social://</c> resources, for <see cref="SocialResources"/> and for listen streams
/// (<see cref="SocialSubscriptions"/>), so both see the same content and the same marker. Every
/// payload starts with <c>marker</c>, a short hash of what the resource lists, which changes
/// exactly when that does.
/// </summary>
internal static class SocialResourceReader
{
    /// <summary>How many of the newest mentions and replies a mentions resource lists.</summary>
    public const int MentionLimit = 50;

    /// <summary>How many of the newest unread notifications the unread resource lists.</summary>
    public const int UnreadLimit = 50;

    public static async Task<ResourceRead> ReadAsync(SocialResourceUri resource, IServiceProvider services, CancellationToken ct)
    {
        var queries = services.GetRequiredService<SocialQueries>();
        switch (resource.Kind)
        {
            case SocialResourceKind.Providers:
            {
                // The polling loop's outcomes, not a live check, so the content moves exactly when the
                // loop announces a provider started or stopped failing.
                var outcomes = services.GetRequiredService<ProviderPollOutcomes>();
                var providers = services.GetServices<ISocialMediaProvider>()
                    .Select(p => (Provider: p, Outcome: outcomes.Get(p.ProviderId)))
                    .Select(x => new ProviderPollStatus(x.Provider.ProviderId, x.Provider.ProviderName,
                        x.Outcome?.Succeeded, x.Outcome?.At))
                    .ToList();
                var marker = Marker(providers.Select(p => $"{p.ProviderId}:{p.LastPollSucceeded}"));
                return Snapshot(resource, marker, new { marker, providers });
            }

            case SocialResourceKind.Mentions:
            {
                var (selection, _) = await queries.ResolveProviderAsync(resource.ProviderId, ct);
                if (!selection.IsValid)
                {
                    return new ResourceRead(null, selection.Error);
                }
                var mentions = await queries.GetRecentMentionsAsync(MentionLimit, selection.Filter, since: null, ct);
                var marker = Marker(mentions.Select(n => n.Id));
                return Snapshot(resource with { ProviderId = selection.Filter }, marker,
                    new { marker, providerId = selection.Filter ?? "all", mentions });
            }

            case SocialResourceKind.Unread:
            {
                // Capped like mentions: Threads notifications are never read, so the set only grows.
                var repository = services.GetRequiredService<ISocialDataRepository>();
                var count = await repository.CountUnreadNotificationsAsync(providerId: null, ct);
                var notifications = await repository.GetUnreadNotificationsAsync(providerId: null, UnreadLimit, ct);
                var marker = Marker(notifications.Select(n => n.Id).Prepend(count.ToString(CultureInfo.InvariantCulture)));
                return Snapshot(resource, marker, new { marker, count, notifications });
            }

            case SocialResourceKind.Post:
            {
                var (selection, known) = await queries.ResolveProviderAsync(resource.ProviderId, ct);
                if (!selection.IsValid || selection.Filter is null)
                {
                    return new ResourceRead(null, selection.Error
                        ?? $"A post resource names one provider. Valid values: {string.Join(", ", known)}.");
                }
                var post = await services.GetRequiredService<ISocialDataRepository>()
                    .GetPostAsync(selection.Filter, resource.PostId!, ct);
                if (post is null)
                {
                    return new ResourceRead(null,
                        $"No stored {selection.Filter} post has the id '{resource.PostId}'. Use a post's platformPostId from get_top_posts.");
                }
                var marker = Marker([$"{post.Id}:{post.LikeCount}:{post.RepostCount}:{post.ReplyCount}"]);
                return Snapshot(resource with { ProviderId = selection.Filter }, marker, new { marker, post });
            }

            default:
                return new ResourceRead(null, "Unknown resource.");
        }
    }

    private static ResourceRead Snapshot<T>(SocialResourceUri resolved, string marker, T payload) =>
        new(new ResourceSnapshot(ToolJson.Serialize(payload), marker, resolved), null);

    /// <summary>The first 16 hex characters of a SHA-256 over the items, in order.</summary>
    internal static string Marker(IEnumerable<string> items)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", items)));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
