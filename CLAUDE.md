# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test Commands

```bash
# Build
dotnet build SocialAgent.slnx

# Run all tests
dotnet test SocialAgent.slnx

# Run a single test by fully qualified name
dotnet test SocialAgent.slnx --filter "FullyQualifiedName~AnalyticsServiceTests.GetEngagementSummary_WithPosts_CalculatesAverages"

# Run tests by class
dotnet test SocialAgent.slnx --filter "ClassName~AnalyticsServiceTests"

# Run with verbose output
dotnet test SocialAgent.slnx --verbosity normal
```

## Architecture

SocialAgent is an **A2A-enabled social media monitoring agent** built on .NET 10. It uses the **Microsoft Agents Framework** (`Microsoft.Agents.Hosting.AspNetCore.A2A.Preview`) for A2A protocol support and runs as an ASP.NET Core application in Kubernetes.

**Core design principle:** Plugin-based provider architecture with normalized data models. Each social media platform is an independent provider assembly.

### Project Structure

- **`src/SocialAgent.Core/`** — Domain models (`SocialPost`, `SocialNotification`, `SocialProfile`, `EngagementSummary`), provider interface (`ISocialMediaProvider`), analytics interface (`IAnalyticsService`)
- **`src/SocialAgent.Data/`** — EF Core `SocialAgentDbContext`, repository pattern (`ISocialDataRepository`), supports PostgreSQL (prod) and SQLite (dev)
- **`src/SocialAgent.Analytics/`** — Analytics engine computing engagement summaries, top posts, follower insights, platform comparisons
- **`src/SocialAgent.Providers.Mastodon/`** — Mastodon REST API client implementing `ISocialMediaProvider`
- **`src/SocialAgent.Providers.Bluesky/`** — Bluesky AT Protocol client implementing `ISocialMediaProvider`
- **`src/SocialAgent.Host/`** — ASP.NET Core host, A2A agent handler (`SocialAgentHandler`), background polling service
- **`tests/`** — MSTest unit tests with NSubstitute for mocking
- **`deploy/k8s/`** — Kubernetes manifests (Deployment, Service, ConfigMap, Secret)

### A2A Protocol

The agent exposes the Google A2A protocol via the Microsoft Agents SDK:
- **`GET /.well-known/agent-card.json`** — Agent card with skills
- **`POST /a2a`** — JSON-RPC message handling

Seven skills are exposed: `engagement-summary`, `top-posts`, `recent-mentions`, `follower-insights`, `platform-comparison`, `check-notifications`, `provider-status`.

### Provider Plugin Pattern

Each provider implements `ISocialMediaProvider` and is registered via DI extension methods. Providers are conditionally enabled via configuration. The polling service iterates all registered providers on a configurable interval.

### Key Conventions

- **Nullable reference types** enabled — respect null-safety throughout
- **ImplicitUsings** enabled — no common using statements needed
- **TreatWarningsAsErrors** enabled — all warnings are build errors
- **Central Package Management** — all package versions in `Directory.Packages.props`
- **Async-first** — all I/O is Task-based
- **NSubstitute** is the mocking framework for tests
- **Configuration** — standard .NET config stack: `appsettings.json`, environment variables, user secrets, k8s Secrets
- **Database** — EF Core with SQLite for dev, PostgreSQL for prod. `DatabaseMigrationService` runs `EnsureCreatedAsync` on startup.

### Future Integration

This agent is designed to collaborate with [RockBot](https://github.com/MarimerLLC/rockbot) via the A2A protocol. RockBot can discover this agent's capabilities via the agent card endpoint and invoke skills via `/a2a`.
