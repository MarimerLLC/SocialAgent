using System.Reflection;
using A2A;
using A2A.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using ModelContextProtocol.Extensions.Apps;
using Serilog;
using SocialAgent.Analytics;
using SocialAgent.Data;
using SocialAgent.Host;
using SocialAgent.Host.Auth;
using SocialAgent.Host.Mcp;
using SocialAgent.Host.Routing;
using SocialAgent.Host.Services;
using SocialAgent.Host.Telemetry;
using SocialAgent.Providers.Bluesky;
using SocialAgent.Providers.Mastodon;
using SocialAgent.Providers.Threads;

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
// The queries every front end (A2A skills, MCP tools and resources) shares.
builder.Services.AddScoped<SocialQueries>();
// What the polling loop saved, for MCP listen streams. In-process, so one replica only.
builder.Services.AddSingleton<ISocialChanges, SocialChanges>();
builder.Services.AddKeyedSingleton<IAgentHandler, SocialAgentA2AHandler>(SocialAgentStubAgent.AgentName);
builder.AddA2AServer(SocialAgentStubAgent.AgentName);

// MCP front end at /mcp: the same queries as the A2A skills, plus resources and push updates.
builder.Services.Configure<SubscriptionOptions>(builder.Configuration.GetSection(SubscriptionOptions.SectionName));
builder.Services.AddSingleton<ListenStreams>();
builder.Services
    .AddMcpServer(SocialAgentMcpOptions.Configure)
    // Stateless: no tool needs session state. The one question a tool can ask (an unreadable
    // `since`) uses an MRTR input request, which ends the call instead of holding it open, and push
    // updates ride the subscriptions/listen request's own stream (SocialSubscriptions).
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<SocialTools>()
    .WithResources<SocialResources>()
    .WithResources<SocialAppResources>()
    .WithMcpApps();

// Data layer
builder.Services.AddSocialAgentData(builder.Configuration);

// Analytics
builder.Services.AddSocialAgentAnalytics();

// Social media providers
builder.Services.AddMastodonProvider(builder.Configuration);
builder.Services.AddBlueskyProvider(builder.Configuration);
builder.Services.AddThreadsProvider(builder.Configuration);

// Background services
builder.Services.AddHostedService<DatabaseMigrationService>();
if (builder.Configuration.GetValue<bool>("SocialAgent:Providers:Threads:Enabled"))
{
    // Registered after the migration service so the database table exists when the
    // refresh service first reads or writes the persisted token.
    builder.Services.AddHostedService<ThreadsTokenRefreshService>();
}
builder.Services.AddHostedService<SocialMediaPollingService>();
builder.Services.AddHostedService<DataRetentionService>();

// Authentication (API key required in non-Development environments)
builder.Services.AddApiKeyAuthentication(builder.Configuration, builder.Environment);

// LLM skill routing (optional — falls back to keyword matching if not configured)
var llmSection = builder.Configuration.GetSection("LLM:Low");
if (llmSection.Exists() && !string.IsNullOrEmpty(llmSection["ApiKey"]))
{
    builder.Services.Configure<SkillRouterOptions>(options =>
    {
        options.Endpoint = llmSection["Endpoint"] ?? string.Empty;
        options.ApiKey = llmSection["ApiKey"] ?? string.Empty;
        options.ModelId = llmSection["ModelId"] ?? string.Empty;
        if (int.TryParse(llmSection["TimeoutSeconds"], out var timeoutSeconds) && timeoutSeconds > 0)
        {
            options.TimeoutSeconds = timeoutSeconds;
        }
    });
    builder.Services.AddHttpClient<SkillRouter>();
}

// OpenTelemetry
builder.Services.AddSocialAgentTelemetry();

// Health checks. "ready" is tagged so the readiness probe exercises the database while the
// liveness probe stays a pure process check — a database blip should not restart the pod.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<SocialAgentDbContext>("database", tags: ["ready"]);

var app = builder.Build();

app.UseSerilogRequestLogging();

app.UseAuthentication();
app.UseAuthorization();

// Build the agent card. The A2A v1.0 spec example shows interface URLs as absolute URLs, so
// when SocialAgent:PublicBaseUrl is configured we emit absolute URLs there. In dev, where the
// public base is unknown, we fall back to the relative path "/a2a".
var agentVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    // Strip any source-control suffix (e.g. "1.4.0+abc1234") that the SDK appends.
    .Split('+')[0]
    ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
    ?? "1.0.0";
var publicBaseUrl = builder.Configuration["SocialAgent:PublicBaseUrl"]?.TrimEnd('/');
var a2aInterfaceUrl = string.IsNullOrEmpty(publicBaseUrl) ? "/a2a" : $"{publicBaseUrl}/a2a";
var mcpUrl = string.IsNullOrEmpty(publicBaseUrl) ? "/mcp" : $"{publicBaseUrl}/mcp";
var agentCard = new AgentCard
{
    Name = "SocialAgent",
    Description = "Social media monitoring and analytics agent. " +
        "Monitors Mastodon, Bluesky, Threads, and other platforms for posts, mentions, and engagement. " +
        "Provides analytics on engagement trends, top posts, and follower insights. " +
        $"The same data is also served over MCP at {mcpUrl} (Streamable HTTP, same API key), " +
        "with resources that push an update when new mentions or replies arrive.",
    Version = agentVersion,
    Skills = [.. SkillCatalog.AgentCardSkills],
    DefaultInputModes = ["text"],
    DefaultOutputModes = ["text"],
    // Capabilities is required by the A2A 1.0 card schema. Declare it explicitly: SDK 1.0.0-preview4
    // omits the property when it is left unset, which strict clients reject. Each skill answers
    // with one synchronous message, so neither streaming nor push notifications is offered.
    Capabilities = new AgentCapabilities { Streaming = false, PushNotifications = false },
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
// MCP (Streamable HTTP) on the same port and API key as A2A.
var mcpEndpoints = app.MapMcp("/mcp");

if (requireAuth)
{
    var policy = new AuthorizationPolicyBuilder(ApiKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser()
        .Build();
    jsonRpcEndpoints.RequireAuthorization(policy);
    httpJsonEndpoints.RequireAuthorization(policy);
    mcpEndpoints.RequireAuthorization(policy);
    // Agent card stays anonymous so clients can discover capabilities without credentials.
}

// Health check endpoints (anonymous)
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});

app.Run();

public partial class Program { }
