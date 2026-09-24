using System.Net;
using System.Text.RegularExpressions;

namespace SocialAgent.Core.Text;

/// <summary>
/// Converts provider-supplied HTML (Mastodon serves status content as HTML) into the plain
/// text the rest of the pipeline assumes — analytics, A2A skill output, and anything a calling
/// agent renders.
/// </summary>
public static partial class HtmlText
{
    /// <summary>
    /// Strips markup and decodes entities, preserving paragraph and line breaks as newlines.
    /// Returns <see cref="string.Empty"/> for null or blank input.
    /// </summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        // Turn structural breaks into newlines before stripping, otherwise paragraphs run together.
        // Block ends get a blank line so paragraphs stay distinguishable from a plain <br>.
        var withBreaks = LineBreakTags().Replace(html, "\n");
        withBreaks = BlockEndTags().Replace(withBreaks, "\n\n");
        var stripped = AnyTag().Replace(withBreaks, string.Empty);
        var decoded = WebUtility.HtmlDecode(stripped);

        // Collapse the runs of blank lines the <p></p> pattern leaves behind.
        return ExcessNewlines().Replace(decoded, "\n\n").Trim();
    }

    [GeneratedRegex(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTags();

    [GeneratedRegex(@"<\s*/\s*(p|div|li|blockquote)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndTags();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessNewlines();
}
