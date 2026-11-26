using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;

namespace SocialAgent.Host.Mcp;

/// <summary>
/// The JSON shape MCP tools and resources return: camelCase properties, nulls omitted to keep
/// results compact for agents, not indented. (A2A keeps its own PascalCase, indented shape.)
/// </summary>
internal static class ToolJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Readable output for agents: characters like + and ' stay literal instead of becoming JSON
        // escape sequences. Safe because results are never embedded in HTML by the server.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// A tool result: one text block holding a summary line and the compact JSON, and the same
    /// object as <c>structuredContent</c>. The JSON is in the text too because some clients (such
    /// as RockBot's bridge) pass only the text block to the model.
    /// </summary>
    public static CallToolResult Result<T>(string summary, T value) where T : notnull => new()
    {
        Content = [new TextContentBlock { Text = summary + "\n" + Serialize(value) }],
        StructuredContent = JsonSerializer.SerializeToElement(value, Options),
    };

    /// <summary>
    /// A rejected call: <c>isError</c>, with the <c>{error, validProviderIds}</c> object the A2A
    /// skills also answer with. No <c>structuredContent</c>, since it doesn't match the output schema.
    /// </summary>
    public static CallToolResult Error(string error, IReadOnlyList<string> validProviderIds) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = error + "\n" + Serialize(new { error, validProviderIds }) }],
    };
}
