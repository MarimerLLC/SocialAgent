namespace SocialAgent.Host;

/// <summary>
/// The outcome of resolving a caller's <c>providerId</c>: either a repository filter (null for
/// every provider, or one canonical provider id), or an error message to send back to the caller.
/// </summary>
internal readonly record struct ProviderSelectionResult(bool IsValid, string? Filter, string? Error)
{
    public static ProviderSelectionResult All => new(true, null, null);
    public static ProviderSelectionResult One(string providerId) => new(true, providerId, null);
    public static ProviderSelectionResult Invalid(string error) => new(false, null, error);
}

/// <summary>
/// Turns a caller's <c>providerId</c> into a repository filter. The repository filters by exact,
/// case-sensitive id, so a value it doesn't recognise (<c>"Mastodon"</c>, <c>"all"</c>,
/// <c>"mastodon,bluesky"</c>) used to match nothing and silently return an empty result. Every
/// front end (A2A, and later MCP) resolves through here before querying.
/// </summary>
internal static class ProviderSelection
{
    private static readonly string[] AllKeywords = ["all", "*"];

    /// <summary>
    /// The provider ids a caller may name: the enabled providers, then any other provider that
    /// still has stored posts or notifications (one disabled since, whose history has not aged
    /// out). Enabled ids come first, in registration order; the rest are sorted.
    /// </summary>
    public static IReadOnlyList<string> KnownProviderIds(IEnumerable<string> enabled, IEnumerable<string> stored)
    {
        var known = enabled.Distinct(StringComparer.Ordinal).ToList();
        known.AddRange(stored
            .Where(id => !known.Contains(id, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));
        return known;
    }

    /// <summary>
    /// Resolves <paramref name="requested"/> against <paramref name="knownProviderIds"/>, which must
    /// include every provider that has stored rows (see <see cref="KnownProviderIds"/>). Then "every
    /// provider", a null filter, means exactly the providers the caller could have named.
    /// <list type="bullet">
    /// <item>Absent, blank, <c>all</c> or <c>*</c> (any case) mean every provider.</item>
    /// <item>One id, matched case-insensitively and trimmed, means that provider.</item>
    /// <item>A comma-separated list means every provider when it names all the known ones (or
    /// includes <c>all</c>), or that provider when it names just one. Any other subset is an
    /// error, because the repository filters by a single provider.</item>
    /// <item>Any part that matches no known provider is an error listing the valid ids, even
    /// next to <c>all</c>, so a typo never passes unnoticed.</item>
    /// </list>
    /// </summary>
    public static ProviderSelectionResult Resolve(string? requested, IReadOnlyCollection<string> knownProviderIds)
    {
        var parts = (requested ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var matched = new List<string>();
        var namesAll = parts.Length == 0;
        foreach (var part in parts)
        {
            if (AllKeywords.Contains(part, StringComparer.OrdinalIgnoreCase))
            {
                namesAll = true;
                continue;
            }

            var id = knownProviderIds.FirstOrDefault(p => string.Equals(p, part, StringComparison.OrdinalIgnoreCase));
            if (id is null)
            {
                return ProviderSelectionResult.Invalid(UnknownProviderMessage(part, knownProviderIds));
            }
            if (!matched.Contains(id, StringComparer.Ordinal))
            {
                matched.Add(id);
            }
        }

        if (namesAll)
        {
            return ProviderSelectionResult.All;
        }

        if (matched.Count == knownProviderIds.Count)
        {
            // Naming every known provider is the same as naming none. With a single known provider
            // this keeps the filter, which returns the same rows either way.
            return matched.Count == 1 ? ProviderSelectionResult.One(matched[0]) : ProviderSelectionResult.All;
        }

        return matched.Count == 1
            ? ProviderSelectionResult.One(matched[0])
            : ProviderSelectionResult.Invalid(
                $"providerId accepts one provider or all of them, not a subset ('{requested}'). " +
                $"Valid values: {string.Join(", ", knownProviderIds)}. Omit providerId, or use 'all', for every provider.");
    }

    private static string UnknownProviderMessage(string part, IReadOnlyCollection<string> knownProviderIds) =>
        knownProviderIds.Count == 0
            ? $"Unknown providerId '{part}': no social media providers are enabled. Omit providerId."
            : $"Unknown providerId '{part}'. Valid values: {string.Join(", ", knownProviderIds)}. " +
              "Omit providerId, or use 'all', for every provider.";
}
