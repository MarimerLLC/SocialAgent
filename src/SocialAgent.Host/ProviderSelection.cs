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
    /// Resolves <paramref name="requested"/> against the enabled providers' ids.
    /// <list type="bullet">
    /// <item>Absent, blank, <c>all</c> or <c>*</c> (any case) mean every provider.</item>
    /// <item>One id, matched case-insensitively and trimmed, means that provider.</item>
    /// <item>A comma-separated list means every provider when it names all the enabled ones (or
    /// includes <c>all</c>), or that provider when it names just one. Any other subset is an
    /// error, because the repository filters by a single provider.</item>
    /// <item>An id that matches no enabled provider is an error listing the valid ids.</item>
    /// </list>
    /// </summary>
    public static ProviderSelectionResult Resolve(string? requested, IReadOnlyCollection<string> enabledProviderIds)
    {
        var parts = (requested ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0 || parts.Any(IsAllKeyword))
        {
            return ProviderSelectionResult.All;
        }

        var matched = new List<string>();
        foreach (var part in parts)
        {
            var id = enabledProviderIds.FirstOrDefault(p => string.Equals(p, part, StringComparison.OrdinalIgnoreCase));
            if (id is null)
            {
                return ProviderSelectionResult.Invalid(UnknownProviderMessage(part, enabledProviderIds));
            }
            if (!matched.Contains(id, StringComparer.Ordinal))
            {
                matched.Add(id);
            }
        }

        if (matched.Count == enabledProviderIds.Count)
        {
            // Naming every enabled provider is the same as naming none. With a single enabled
            // provider this keeps the filter, which returns the same rows either way.
            return matched.Count == 1 ? ProviderSelectionResult.One(matched[0]) : ProviderSelectionResult.All;
        }

        return matched.Count == 1
            ? ProviderSelectionResult.One(matched[0])
            : ProviderSelectionResult.Invalid(
                $"providerId accepts one provider or all of them, not a subset ('{requested}'). " +
                $"Valid values: {string.Join(", ", enabledProviderIds)}. Omit providerId, or use 'all', for every provider.");
    }

    private static bool IsAllKeyword(string part) =>
        AllKeywords.Contains(part, StringComparer.OrdinalIgnoreCase);

    private static string UnknownProviderMessage(string part, IReadOnlyCollection<string> enabledProviderIds) =>
        enabledProviderIds.Count == 0
            ? $"Unknown providerId '{part}': no social media providers are enabled. Omit providerId."
            : $"Unknown providerId '{part}'. Valid values: {string.Join(", ", enabledProviderIds)}. " +
              "Omit providerId, or use 'all', for every provider.";
}
