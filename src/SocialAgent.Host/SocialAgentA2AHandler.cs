using A2A;

namespace SocialAgent.Host;

internal sealed class SocialAgentA2AHandler(SkillDispatcher dispatcher, ILogger<SocialAgentA2AHandler> logger) : IAgentHandler
{
    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(eventQueue);

        var userText = context.UserText ?? string.Empty;

        string responseText;
        try
        {
            responseText = await dispatcher.DispatchAsync(userText, cancellationToken);
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
}
