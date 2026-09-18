# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test Commands

```bash
# Build
dotnet build SocialAgent.slnx

# Run all tests (integration tests self-skip without provider credentials)
dotnet test SocialAgent.slnx

# Run only unit tests, as CI does
dotnet test SocialAgent.slnx --filter "TestCategory!=Integration"

# Run a single test by fully qualified name
dotnet test SocialAgent.slnx --filter "FullyQualifiedName~AnalyticsServiceTests.GetEngagementSummary_WithPosts_CalculatesAverages"

# Run tests by class
dotnet test SocialAgent.slnx --filter "ClassName~AnalyticsServiceTests"

# Run with verbose output
dotnet test SocialAgent.slnx --verbosity normal

# Add a migration (both dialects must stay in step)
dotnet ef migrations add <Name> --project src/SocialAgent.Data.Migrations.Sqlite --context SocialAgentDbContext
dotnet ef migrations add <Name> --project src/SocialAgent.Data.Migrations.Npgsql --context SocialAgentDbContext
```

## Architecture

SocialAgent is an **A2A-enabled social media monitoring agent** built on .NET 10. It hosts the **A2A 1.0** protocol via the **Microsoft Agent Framework** (`Microsoft.Agents.AI.Hosting.A2A.AspNetCore`) and the upstream **A2A .NET SDK** (`A2A`, `A2A.AspNetCore`), running as an ASP.NET Core application in Kubernetes.

**Core design principle:** Plugin-based provider architecture with normalized data models. Each social media platform is an independent provider assembly.

### Project Structure

- **`src/SocialAgent.Core/`** — Domain models (`SocialPost`, `SocialNotification`, `SocialProfile`, `EngagementSummary`), provider interface (`ISocialMediaProvider`), analytics interface (`IAnalyticsService`)
- **`src/SocialAgent.Data/`** — EF Core `SocialAgentDbContext`, repository pattern (`ISocialDataRepository`), supports PostgreSQL (prod) and SQLite (dev)
- **`src/SocialAgent.Data.Migrations.Sqlite/`**, **`src/SocialAgent.Data.Migrations.Npgsql/`** — EF Core migrations. EF cannot resolve two providers' migrations from a single assembly, so each dialect has its own; `AddSocialAgentData` points the context at the right one via `MigrationsAssembly`
- **`src/SocialAgent.Analytics/`** — Analytics engine computing engagement summaries, top posts, follower insights, platform comparisons
- **`src/SocialAgent.Providers.Mastodon/`** — Mastodon REST API client implementing `ISocialMediaProvider`
- **`src/SocialAgent.Providers.Bluesky/`** — Bluesky AT Protocol client implementing `ISocialMediaProvider`
- **`src/SocialAgent.Providers.Threads/`** — Threads Graph API v1.0 client implementing `ISocialMediaProvider`. Adds `ThreadsTokenStore` (in-memory current token + expiry) and a `RefreshTokenAsync` method on the provider; the host owns the refresh schedule via `ThreadsTokenRefreshService`.
- **`src/SocialAgent.Host/`** — ASP.NET Core host, A2A request handler (`SocialAgentA2AHandler` implementing `A2A.IAgentHandler`), skill dispatcher (`SkillDispatcher`), skill metadata (`SkillCatalog`), stub `AIAgent` (`SocialAgentStubAgent`, framework-required name carrier), background polling service, and `ThreadsTokenRefreshService` (registered only when Threads is enabled — seeds the token from DB on startup, refreshes ahead of `RefreshThresholdDays`, persists via `ISocialDataRepository`)
- **`tests/`** — MSTest unit tests with NSubstitute for mocking. `SocialAgent.TestSupport` provides `StubHttpMessageHandler` (records requests, replays canned responses) and `StubHttpClientFactory` for provider tests. Tests marked `[TestCategory("Integration")]` hit live APIs and call `Assert.Inconclusive` when their credentials are absent
- **`deploy/k8s/`** — Kubernetes manifests (Deployment, Service, ConfigMap, Secret). The pod runs non-root with a read-only root filesystem

### A2A Protocol

The agent speaks **A2A protocol version 1.0**. Endpoints:
- **`GET /.well-known/agent-card.json`** — Agent card (A2A 1.0 discovery path)
- **`POST /a2a`** — JSON-RPC binding (methods: `SendMessage`, `GetTask`, etc., per `A2A.A2AMethods`)
- **`POST /a2a/message:send`**, **`/a2a/tasks/{id}`**, etc. — HTTP+JSON binding routes

Both transports are mapped to the same `/a2a` prefix; clients pick whichever they prefer.

Seven skills are exposed: `engagement-summary`, `top-posts`, `recent-mentions`, `follower-insights`, `platform-comparison`, `check-notifications`, `provider-status`. Skill dispatch is deterministic (keyword match, optionally LLM-assisted via `SkillRouter`) — there is no LLM in the request path by default. The `AIAgent` registered with the framework is a stub used only as a name carrier; all request handling flows through `SocialAgentA2AHandler`.

### Provider Plugin Pattern

Each provider implements `ISocialMediaProvider` and is registered via DI extension methods. Providers are conditionally enabled via configuration. The polling service iterates all registered providers on a configurable interval.

**Provider HTTP conventions** — these exist because violating them caused production bugs:

- A provider is a **singleton** (so cached state such as the Bluesky session or the Mastodon account id is shared), but it takes `IHttpClientFactory` and resolves a client **per call**. Never inject `HttpClient` into a singleton: `AddHttpClient<T>` registers `T` as transient, and capturing it in a singleton pins the message handler for the process lifetime, which defeats handler rotation and leaves DNS stale.
- Set `Authorization` on the `HttpRequestMessage`, never on `HttpClient.DefaultRequestHeaders`. The handler is pooled and shared between the polling loop and A2A request threads; `HttpHeaders` is not thread-safe.
- Never put a credential in a URL. Request URIs reach OpenTelemetry spans and exception messages.
- Providers page until they pass the `since` cutoff, bounded by a `MaxPages` constant. A single fixed page silently drops data when a poll interval is busy.
- Provider HTTP clients get `AddStandardResilienceHandler()` and a 30s timeout.
- Provider options are validated with `ValidateOnStart` so misconfiguration fails at startup.

### Key Conventions

- **Nullable reference types** enabled — respect null-safety throughout
- **ImplicitUsings** enabled — no common using statements needed
- **TreatWarningsAsErrors** enabled — all warnings are build errors
- **Central Package Management** — all package versions in `Directory.Packages.props`
- **Async-first** — all I/O is Task-based
- **NSubstitute** is the mocking framework for tests
- **Configuration** — standard .NET config stack: `appsettings.json`, environment variables, user secrets, k8s Secrets
- **Database** — EF Core with SQLite for dev, PostgreSQL for prod. Schema changes go through **EF Core migrations**: add the migration to *both* `SocialAgent.Data.Migrations.Sqlite` and `SocialAgent.Data.Migrations.Npgsql`, or the other dialect silently falls behind. `DatabaseMigrationService` applies pending migrations at startup, and adopts a pre-1.5.0 database (schema present, no `__EFMigrationsHistory`) by recording the baseline as already applied. Note: `IHistoryRepository.ExistsAsync()` reports **true on Npgsql even when the history table is absent**, so that detection queries the catalogue directly — verified against a real PostgreSQL 17 server, not just SQLite.
- **Analytics** — aggregate in SQL via the `ISocialDataRepository` aggregate methods. Do not pull a retention window into memory to sum it.
- **Health checks** — `/health/ready` is tagged `ready` and exercises the database; `/health/live` deliberately runs no checks, so a database blip does not restart the pod.

### Future Integration

This agent is designed to collaborate with [RockBot](https://github.com/MarimerLLC/rockbot) via the A2A protocol. RockBot can discover this agent's capabilities via the agent card endpoint and invoke skills via `/a2a`.
