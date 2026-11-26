using System.ComponentModel;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Server;

namespace SocialAgent.Host.Mcp;

/// <summary>
/// MCP Apps views (the <c>io.modelcontextprotocol/ui</c> extension): HTML a host that supports it
/// renders in place of a tool's raw result. <c>get_recent_mentions</c> points at the mentions card
/// through its <c>_meta.ui.resourceUri</c>. Other clients ignore both.
/// </summary>
[McpServerResourceType]
internal sealed class SocialAppResources
{
    public const string MentionsViewUri = "ui://socialagent/mentions.html";

    private static readonly Lazy<string> MentionsHtml = new(() =>
    {
        using var stream = typeof(SocialAppResources).Assembly.GetManifestResourceStream("SocialAgent.Host.Mcp.mentions.html")
            ?? throw new InvalidOperationException("The mentions view is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    [McpServerResource(UriTemplate = MentionsViewUri, Name = "mentions_view", Title = "Mentions card", MimeType = McpApps.HtmlMimeType)]
    [Description("The card a host that supports MCP Apps shows for get_recent_mentions: who, where, when, the text and a link to each post.")]
    public string MentionsView() => MentionsHtml.Value;
}
