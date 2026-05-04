using System.Text.Json;
using A2A;

namespace SocialAgent.Host;

internal sealed class SocialAgentA2AHandler(SkillDispatcher dispatcher, ILogger<SocialAgentA2AHandler> logger) : IAgentHandler
{
    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(eventQueue);

        var userText = context.UserText ?? string.Empty;
        var explicitSkillId = ReadSkillIdFromMetadata(context);
        var parameters = context.Message?.Metadata;

        string responseText;
        try
        {
            responseText = await dispatcher.DispatchAsync(explicitSkillId, userText, parameters, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Skill dispatch failed for input \"{Input}\"", userText);
            responseText = $"Error executing skill: {ex.Message}";
        }

        var reply = new Message
        {
            MessageId = Guid.NewGuid().ToString("N"),
            ContextId = context.ContextId,
            Role = Role.Agent,
            Parts = [new Part { Text = responseText }]
        };

        await eventQueue.EnqueueMessageAsync(reply, cancellationToken);
    }

    // Skill ID can arrive on the message (per RockBot's BuildV1SendRequest) or on the request envelope.
    // The "skill" key on either Metadata dictionary wins; an "skillId" key is also accepted as a fallback
    // because the spec doesn't yet standardize the field name and clients vary.
    private static string? ReadSkillIdFromMetadata(RequestContext context)
    {
        return ReadString(context.Message?.Metadata, "skill")
            ?? ReadString(context.Message?.Metadata, "skillId")
            ?? ReadString(context.Metadata, "skill")
            ?? ReadString(context.Metadata, "skillId");
    }

    private static string? ReadString(Dictionary<string, JsonElement>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var element))
        {
            return null;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }
}
