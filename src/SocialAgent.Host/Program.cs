using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Hosting.A2A;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using Serilog;
using SocialAgent.Analytics;
using SocialAgent.Data;
using SocialAgent.Host;
using SocialAgent.Host.Auth;
using SocialAgent.Host.Services;
using SocialAgent.Host.Telemetry;
using SocialAgent.Providers.Bluesky;
using SocialAgent.Providers.Mastodon;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddUserSecrets<Program>(optional: true);

// Serilog
builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration));

// Agent infrastructure
builder.Services.AddSingleton<IStorage, MemoryStorage>();
builder.AddAgentApplicationOptions();

// Register the agent
builder.AddAgent<SocialAgentHandler>();

// Register A2A adapter
builder.Services.AddA2AAdapter();

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

// Authentication (API key required in non-Development environments)
builder.Services.AddApiKeyAuthentication(builder.Configuration);

// OpenTelemetry
builder.Services.AddSocialAgentTelemetry();

// Health checks
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseSerilogRequestLogging();

app.UseAuthentication();
app.UseAuthorization();

// Map A2A endpoints (no auth required for development)
app.MapA2AEndpoints(requireAuth: !app.Environment.IsDevelopment());

// Health check endpoints
app.MapHealthChecks("/health/ready");
app.MapHealthChecks("/health/live");

app.Run();
