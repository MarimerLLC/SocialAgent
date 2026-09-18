# SocialAgent

An A2A-enabled social media monitoring and analytics agent.

## What is SocialAgent?

SocialAgent monitors your social media accounts across multiple platforms, collects engagement data, and provides analytics — all accessible via the [A2A (Agent-to-Agent)](https://google.github.io/A2A/) protocol. It has no UI; you interact with it through other A2A-capable agents like [RockBot](https://github.com/MarimerLLC/rockbot).

### Supported Platforms

| Platform | Status | API |
|---|---|---|
| Mastodon | ✅ Implemented | REST API v1 |
| Bluesky | ✅ Implemented | AT Protocol |
| Threads | ✅ Implemented | Threads Graph API v1.0 (with auto token refresh) |

### A2A Skills

| Skill | Description |
|---|---|
| `engagement-summary` | Engagement metrics across all platforms (likes, reposts, replies, mentions, followers) |
| `top-posts` | Your most-engaged posts ranked by total engagement |
| `recent-mentions` | Recent mentions and replies |
| `follower-insights` | Top engagers and their interaction patterns |
| `platform-comparison` | Side-by-side engagement metrics across platforms |
| `check-notifications` | Unread notifications across all platforms |
| `provider-status` | Health and connectivity of configured providers |

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Social media API credentials (see Configuration below)

### Build & Run

```bash
# Build
dotnet build SocialAgent.slnx

# Run tests (provider integration tests self-skip without credentials)
dotnet test SocialAgent.slnx

# Unit tests only, as CI runs them
dotnet test SocialAgent.slnx --filter "TestCategory!=Integration"

# Run the agent (development mode — no auth required)
cd src/SocialAgent.Host
dotnet run
```

The agent starts on `http://localhost:5000` by default (or as configured by `ASPNETCORE_URLS`).

### A2A Endpoints

The agent speaks **A2A protocol version 1.0**.

- `GET /.well-known/agent-card.json` — Agent card (A2A 1.0 discovery)
- `POST /a2a` — JSON-RPC binding (`SendMessage`, `GetTask`, etc.)
- `POST /a2a/message:send`, `GET /a2a/tasks/{id}`, etc. — HTTP+JSON binding
- `GET /health/ready` — Readiness probe (checks database connectivity)
- `GET /health/live` — Liveness probe (process only, so a database blip does not restart the pod)

### Configuration

Outside the `Development` environment, `Authentication:ApiKey` is required — the agent refuses to
start without it rather than running and rejecting every request.

Configure providers via `appsettings.json`, environment variables, or user secrets:

```bash
# Set Mastodon credentials via user secrets
cd src/SocialAgent.Host
dotnet user-secrets set "SocialAgent:Providers:Mastodon:Enabled" "true"
dotnet user-secrets set "SocialAgent:Providers:Mastodon:InstanceUrl" "https://mastodon.social"
dotnet user-secrets set "SocialAgent:Providers:Mastodon:AccessToken" "your-token"

# Set Bluesky credentials
dotnet user-secrets set "SocialAgent:Providers:Bluesky:Enabled" "true"
dotnet user-secrets set "SocialAgent:Providers:Bluesky:Handle" "you.bsky.social"
dotnet user-secrets set "SocialAgent:Providers:Bluesky:AppPassword" "your-app-password"

# Set Threads credentials (long-lived token; auto-refreshes ahead of expiry)
dotnet user-secrets set "SocialAgent:Providers:Threads:Enabled" "true"
dotnet user-secrets set "SocialAgent:Providers:Threads:AccessToken" "your-long-lived-token"
```

For Threads-specific setup (OAuth scopes, Meta App Review,
`IncludePostInsights` tradeoffs, token-refresh behavior), see
[`docs/providers/threads.md`](docs/providers/threads.md).

### Database

- **Development:** SQLite (default, zero config)
- **Production:** PostgreSQL (set `SocialAgent:DatabaseProvider` to `PostgreSQL` and provide `ConnectionStrings:SocialAgent`)

Schema is managed by EF Core migrations, applied automatically at startup. Because EF cannot
resolve two providers' migrations from one assembly, each dialect has its own project — add every
migration to both:

```bash
dotnet ef migrations add <Name> --project src/SocialAgent.Data.Migrations.Sqlite --context SocialAgentDbContext
dotnet ef migrations add <Name> --project src/SocialAgent.Data.Migrations.Npgsql --context SocialAgentDbContext
```

Databases created before 1.5.0 were provisioned by `EnsureCreated` and have no migrations history.
The first 1.5.0 start adopts them by recording the baseline migration as already applied; no data is
modified. **Back up before the first production rollout.**

## Kubernetes Deployment

```bash
# Apply manifests
kubectl apply -f deploy/k8s/namespace.yaml
kubectl apply -f deploy/k8s/secret.yaml    # Edit with real credentials first!
kubectl apply -f deploy/k8s/configmap.yaml
kubectl apply -f deploy/k8s/deployment.yaml
kubectl apply -f deploy/k8s/service.yaml
```

The agent runs as a continuous Deployment (not CronJob) for A2A responsiveness. The pod runs as a
non-root user with a read-only root filesystem, dropped capabilities and the `RuntimeDefault`
seccomp profile.

## Architecture

```
┌──────────────────────────────────────────────────────────────────────┐
│  SocialAgent.Host (ASP.NET Core)                                     │
│                                                                      │
│  ┌──────────────┐  ┌──────────────────────────────────────────────┐  │
│  │ A2A 1.0      │  │ Background Services                          │  │
│  │ (MS Agent    │  │ ┌───────────┐ ┌──────────┐ ┌──────────────┐ │  │
│  │  Framework + │  │ │ Mastodon  │ │ Bluesky  │ │   Threads    │ │  │
│  │  A2A SDK)    │  │ │ Provider  │ │ Provider │ │   Provider   │ │  │
│  └──────┬───────┘  │ └─────┬─────┘ └────┬─────┘ └──────┬───────┘ │  │
│         │           │       │            │              │         │  │
│         │           │ + ThreadsTokenRefreshService (auto refresh)│  │
│         │           └───────┼────────────┼──────────────┼─────────┘  │
│  ┌──────▼─────────────────  ▼            ▼              ▼─────────┐  │
│  │            Core (Domain Models & Interfaces)                   │  │
│  └─────────────────────────────┬──────────────────────────────────┘  │
│  ┌─────────────────────────────▼──────────────────────────────────┐  │
│  │       Data (EF Core — PostgreSQL / SQLite)                     │  │
│  └────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────┘
```

## Adding a New Provider

1. Create `src/SocialAgent.Providers.YourPlatform/`
2. Implement `ISocialMediaProvider`
3. Add options class and `ServiceCollectionExtensions`
4. Register in `Program.cs`
5. Add configuration section

## License

MIT
