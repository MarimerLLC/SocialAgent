using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.A2A;
using Microsoft.Agents.Hosting.AspNetCore;
using SocialAgent.Analytics;
using SocialAgent.Data;
using SocialAgent.Host;
using SocialAgent.Host.Services;
using SocialAgent.Providers.Bluesky;
using SocialAgent.Providers.Mastodon;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddUserSecrets<Program>(optional: true);

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

// Health checks
builder.Services.AddHealthChecks();

var app = builder.Build();

// Map A2A endpoints (no auth required for development)
app.MapA2AEndpoints(requireAuth: !app.Environment.IsDevelopment());

// Health check endpoints
app.MapHealthChecks("/health/ready");
app.MapHealthChecks("/health/live");

app.Run();
