using System.Reflection;
using A2A;
using A2A.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Serilog;
using SocialAgent.Analytics;
using SocialAgent.Data;
using SocialAgent.Host;
using SocialAgent.Host.Auth;
using SocialAgent.Host.Routing;
using SocialAgent.Host.Services;
using SocialAgent.Host.Telemetry;
using SocialAgent.Providers.Bluesky;
using SocialAgent.Providers.Mastodon;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddUserSecrets<Program>(optional: true);

// Serilog
builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration));

// Register the stub AIAgent and the custom A2A handler under the same agent name.
// The framework's AddA2AServer wires its A2AServer keyed by this name; because we register a
// keyed IAgentHandler here, the framework's default A2AAgentHandler (which would call into the
// AIAgent) is bypassed entirely.
builder.Services.AddKeyedSingleton<Microsoft.Agents.AI.AIAgent, SocialAgentStubAgent>(SocialAgentStubAgent.AgentName);
builder.Services.AddSingleton<SkillDispatcher>();
builder.Services.AddKeyedSingleton<IAgentHandler, SocialAgentA2AHandler>(SocialAgentStubAgent.AgentName);
builder.AddA2AServer(SocialAgentStubAgent.AgentName);

// Data layer
builder.Services.AddSocialAgentData(builder.Configuration);

// Analytics
builder.Services.AddSocialAgentAnalytics();

// Social media providers
builder.Services.AddMastodonProvider(builder.Configuration);
builder.Services.AddBlueskyProvider(builder.Configuration);

// Background services
builder.Services.AddHostedService<DatabaseMigrationService>();
builder.Services.AddHostedService<SocialMediaPollingService>();
builder.Services.AddHostedService<DataRetentionService>();

// Authentication (API key required in non-Development environments)
builder.Services.AddApiKeyAuthentication(builder.Configuration);

// LLM skill routing (optional — falls back to keyword matching if not configured)
var llmSection = builder.Configuration.GetSection("LLM:Low");
if (llmSection.Exists() && !string.IsNullOrEmpty(llmSection["ApiKey"]))
{
    builder.Services.Configure<SkillRouterOptions>(options =>
    {
        options.Endpoint = llmSection["Endpoint"] ?? string.Empty;
        options.ApiKey = llmSection["ApiKey"] ?? string.Empty;
        options.ModelId = llmSection["ModelId"] ?? string.Empty;
    });
    builder.Services.AddHttpClient<SkillRouter>();
}

// OpenTelemetry
builder.Services.AddSocialAgentTelemetry();

// Health checks
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseSerilogRequestLogging();

app.UseAuthentication();
app.UseAuthorization();

// Build the agent card. The A2A v1.0 spec example shows interface URLs as absolute URLs, so
// when SocialAgent:PublicBaseUrl is configured we emit absolute URLs there. In dev, where the
// public base is unknown, we fall back to the relative path "/a2a".
var agentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
var publicBaseUrl = builder.Configuration["SocialAgent:PublicBaseUrl"]?.TrimEnd('/');
var a2aInterfaceUrl = string.IsNullOrEmpty(publicBaseUrl) ? "/a2a" : $"{publicBaseUrl}/a2a";
var agentCard = new AgentCard
{
    Name = "SocialAgent",
    Description = "Social media monitoring and analytics agent. " +
        "Monitors Mastodon, Bluesky, and other platforms for posts, mentions, and engagement. " +
        "Provides analytics on engagement trends, top posts, and follower insights.",
    Version = agentVersion,
    Skills = [.. SkillCatalog.AgentCardSkills],
    DefaultInputModes = ["text"],
    DefaultOutputModes = ["text"],
    SupportedInterfaces =
    [
        new AgentInterface { Url = a2aInterfaceUrl, ProtocolBinding = "JSONRPC", ProtocolVersion = "1.0" },
        new AgentInterface { Url = a2aInterfaceUrl, ProtocolBinding = "HTTPJSON", ProtocolVersion = "1.0" }
    ]
};

// Map A2A endpoints. Both transports share the /a2a path — JSON-RPC handles POST /a2a, HTTP+JSON
// handles the spec routes (e.g., /a2a/tasks/{id}, /a2a/message:send).
var requireAuth = !app.Environment.IsDevelopment();
var jsonRpcEndpoints = app.MapA2AJsonRpc(SocialAgentStubAgent.AgentName, "/a2a");
var httpJsonEndpoints = app.MapA2AHttpJson(SocialAgentStubAgent.AgentName, "/a2a");
var agentCardEndpoints = app.MapWellKnownAgentCard(agentCard);

if (requireAuth)
{
    var policy = new AuthorizationPolicyBuilder(ApiKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser()
        .Build();
    jsonRpcEndpoints.RequireAuthorization(policy);
    httpJsonEndpoints.RequireAuthorization(policy);
    // Agent card stays anonymous so clients can discover capabilities without credentials.
}

// Health check endpoints (anonymous)
app.MapHealthChecks("/health/ready");
app.MapHealthChecks("/health/live");

app.Run();

public partial class Program { }
