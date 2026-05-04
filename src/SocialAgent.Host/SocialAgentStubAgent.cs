using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace SocialAgent.Host;

/// <summary>
/// Minimal AIAgent registered solely to satisfy the Microsoft Agent Framework's keyed-service
/// requirement. The actual A2A request handling is provided by <see cref="SocialAgentA2AHandler"/>,
/// which is registered as a keyed <see cref="A2A.IAgentHandler"/> with the same name. The framework
/// only reads <see cref="Name"/> from this instance; none of the abstract members are ever invoked.
/// </summary>
internal sealed class SocialAgentStubAgent : AIAgent
{
    public const string AgentName = "social-agent";

    public override string Name => AgentName;

    public override string Description => "Social media monitoring agent (custom A2A handler — AIAgent surface unused).";

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("SocialAgentStubAgent has no AIAgent runtime — use the keyed IAgentHandler instead.");

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("SocialAgentStubAgent has no AIAgent runtime — use the keyed IAgentHandler instead.");

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("SocialAgentStubAgent has no AIAgent runtime — use the keyed IAgentHandler instead.");

    protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("SocialAgentStubAgent has no AIAgent runtime — use the keyed IAgentHandler instead.");

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("SocialAgentStubAgent has no AIAgent runtime — use the keyed IAgentHandler instead.");
}
