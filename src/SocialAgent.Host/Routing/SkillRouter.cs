using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace SocialAgent.Host.Routing;

public class SkillRouter(HttpClient httpClient, IOptions<SkillRouterOptions> options, ILogger<SkillRouter> logger)
{
    private readonly SkillRouterOptions _options = options.Value;

    public record SkillDefinition(string Id, string Name, string Description);

    public async Task<string?> RouteAsync(string userMessage, IReadOnlyList<SkillDefinition> skills, CancellationToken ct)
    {
        var skillList = new StringBuilder();
        foreach (var skill in skills)
        {
            skillList.AppendLine($"- {skill.Id}: {skill.Name} — {skill.Description}");
        }

        var systemPrompt = $"""
            You are a skill router. Given a user message, determine which skill best matches their intent.

            Available skills:
            {skillList}

            Respond with ONLY the skill ID (e.g. "recent-mentions") that best matches.
            If no skill matches, respond with "unknown".
            Do not explain your reasoning. Just the skill ID.
            """;

        var requestBody = new
        {
            model = _options.ModelId,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            },
            max_tokens = 50,
            temperature = 0.0
        };

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_options.Endpoint), "chat/completions"))
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Authorization", $"Bearer {_options.ApiKey}");

        try
        {
            var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(ct);
            var result = json?.Choices?.FirstOrDefault()?.Message?.Content?.Trim().ToLowerInvariant();

            if (string.IsNullOrEmpty(result))
                return null;

            // Validate the result is actually a known skill ID
            var matched = skills.FirstOrDefault(s => result.Contains(s.Id));
            if (matched != null)
            {
                logger.LogInformation("LLM routed \"{Message}\" to skill {SkillId}", userMessage, matched.Id);
                return matched.Id;
            }

            logger.LogWarning("LLM returned unrecognized skill \"{Result}\" for \"{Message}\"", result, userMessage);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "LLM skill routing failed for \"{Message}\", falling back to keyword matching", userMessage);
            return null;
        }
    }

    private class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<Choice>? Choices { get; set; }
    }

    private class Choice
    {
        [JsonPropertyName("message")]
        public MessageContent? Message { get; set; }
    }

    private class MessageContent
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}
