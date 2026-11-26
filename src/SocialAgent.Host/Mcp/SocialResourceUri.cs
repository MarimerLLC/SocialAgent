using SocialAgent.Host.Services;

namespace SocialAgent.Host.Mcp;

internal enum SocialResourceKind
{
    /// <summary><c>social://providers</c></summary>
    Providers,

    /// <summary><c>social://mentions</c> or <c>social://mentions/{providerId}</c></summary>
    Mentions,

    /// <summary><c>social://notifications/unread</c></summary>
    Unread,

    /// <summary><c>social://posts/{providerId}/{postId}</c></summary>
    Post,
}

/// <summary>
/// The resource URIs SocialAgent serves and can push updates for. <see cref="ProviderId"/> is as
/// written in the URI until resolved; <see cref="PostId"/> is the platform post id, unescaped (a
/// Bluesky id is an <c>at://</c> URI, so it is percent-escaped in the resource URI).
/// </summary>
internal sealed record SocialResourceUri(SocialResourceKind Kind, string? ProviderId = null, string? PostId = null)
{
    public const string Scheme = "social://";
    public const string ProvidersUri = Scheme + "providers";
    public const string AllMentionsUri = Scheme + "mentions";
    public const string MentionsTemplate = Scheme + "mentions/{providerId}";
    public const string UnreadUri = Scheme + "notifications/unread";
    public const string PostTemplate = Scheme + "posts/{providerId}/{postId}";

    private const int MaxPartLength = 512;

    public static string Mentions(string? providerId) =>
        providerId is null ? AllMentionsUri : AllMentionsUri + "/" + Uri.EscapeDataString(providerId);

    public static string Post(string providerId, string postId) =>
        Scheme + "posts/" + Uri.EscapeDataString(providerId) + "/" + Uri.EscapeDataString(postId);

    /// <summary>The resource a URI names, or null if it isn't one of the forms above.</summary>
    public static SocialResourceUri? TryParse(string? uri)
    {
        if (uri is null || !uri.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = uri[Scheme.Length..].Split('/');
        return parts switch
        {
            ["providers"] => new SocialResourceUri(SocialResourceKind.Providers),
            ["mentions"] => new SocialResourceUri(SocialResourceKind.Mentions),
            ["mentions", var p] when Part(p) is { } id => new SocialResourceUri(SocialResourceKind.Mentions, id),
            ["notifications", "unread"] => new SocialResourceUri(SocialResourceKind.Unread),
            ["posts", var p, var post] when Part(p) is { } id && Part(post) is { } postId =>
                new SocialResourceUri(SocialResourceKind.Post, id, postId),
            _ => null
        };
    }

    private static string? Part(string escaped)
    {
        if (escaped.Length is 0 or > MaxPartLength)
        {
            return null;
        }
        try
        {
            var value = Uri.UnescapeDataString(escaped);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// True if <paramref name="change"/> may have changed this resource. Only for a resolved
    /// resource: <see cref="ProviderId"/> must be canonical (or null for every provider).
    /// </summary>
    public bool Affects(SocialChange change) => Kind switch
    {
        SocialResourceKind.Providers => change.Kind == SocialChangeKind.Providers,
        SocialResourceKind.Mentions => change.Kind == SocialChangeKind.Mentions
            && (ProviderId is null || string.Equals(ProviderId, change.ProviderId, StringComparison.Ordinal)),
        SocialResourceKind.Unread => change.Kind == SocialChangeKind.Unread,
        SocialResourceKind.Post => change.Kind == SocialChangeKind.Post
            && string.Equals(ProviderId, change.ProviderId, StringComparison.Ordinal)
            && string.Equals(PostId, change.PostId, StringComparison.Ordinal),
        _ => false
    };
}
