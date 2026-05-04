# SocialAgent

An A2A-enabled social media monitoring and analytics agent.

## What is SocialAgent?

SocialAgent monitors your social media accounts across multiple platforms, collects engagement data, and provides analytics — all accessible via the [A2A (Agent-to-Agent)](https://google.github.io/A2A/) protocol. It has no UI; you interact with it through other A2A-capable agents like [RockBot](https://github.com/MarimerLLC/rockbot).

### Supported Platforms

| Platform | Status | API |
|---|---|---|
| Mastodon | ✅ Implemented | REST API v1 |
| Bluesky | ✅ Implemented | AT Protocol |

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

# Run tests
dotnet test SocialAgent.slnx

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
- `GET /health/ready` — Readiness probe
- `GET /health/live` — Liveness probe

### Configuration

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
```

### Database

- **Development:** SQLite (default, zero config)
- **Production:** PostgreSQL (set `SocialAgent:DatabaseProvider` to `PostgreSQL` and provide `ConnectionStrings:SocialAgent`)

## Kubernetes Deployment

```bash
# Apply manifests
kubectl apply -f deploy/k8s/namespace.yaml
kubectl apply -f deploy/k8s/secret.yaml    # Edit with real credentials first!
kubectl apply -f deploy/k8s/configmap.yaml
kubectl apply -f deploy/k8s/deployment.yaml
kubectl apply -f deploy/k8s/service.yaml
```

The agent runs as a continuous Deployment (not CronJob) for A2A responsiveness.

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│  SocialAgent.Host (ASP.NET Core)                        │
│                                                         │
│  ┌──────────────┐  ┌─────────────────────────────────┐  │
│  │ A2A 1.0      │  │ Background Polling Services     │  │
│  │ (MS Agent    │  │ ┌───────────┐ ┌──────────────┐ │  │
│  │  Framework + │  │ │ Mastodon  │ │   Bluesky    │ │  │
│  │  A2A SDK)    │  │ │ Provider  │ │   Provider   │ │  │
│  └──────┬───────┘  │ └─────┬─────┘ └──────┬───────┘ │  │
│         │           └───────┼───────────────┼─────────┘  │
│  ┌──────▼───────────────────▼───────────────▼─────────┐  │
│  │            Core (Domain Models & Interfaces)        │  │
│  └──────────────────────┬─────────────────────────────┘  │
│  ┌──────────────────────▼─────────────────────────────┐  │
│  │       Data (EF Core — PostgreSQL / SQLite)          │  │
│  └─────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────┘
```

## Adding a New Provider

1. Create `src/SocialAgent.Providers.YourPlatform/`
2. Implement `ISocialMediaProvider`
3. Add options class and `ServiceCollectionExtensions`
4. Register in `Program.cs`
5. Add configuration section

## License

MIT
