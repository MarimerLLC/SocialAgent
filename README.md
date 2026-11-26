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
- `POST /mcp` — MCP server (see [MCP Endpoint](#mcp-endpoint))
- `GET /health/ready` — Readiness probe (checks database connectivity)
- `GET /health/live` — Liveness probe (process only, so a database blip does not restart the pod)

### MCP Endpoint

`/mcp` serves the same data over the [Model Context Protocol](https://modelcontextprotocol.io)
(Streamable HTTP, stateless, protocol `2026-07-28`; older clients get everything except push). It
uses the same API key as `/a2a` (`X-Api-Key` header), and needs none in Development.

**Tools** (all read-only). Each returns one summary line plus compact JSON in its text block, and
the same object as `structuredContent` with an output schema:

| Tool | Parameters |
|---|---|
| `get_engagement_summary` | `providerId?`, `since?` (default: last 7 days) |
| `get_top_posts` | `providerId?`, `count?` (default 10), `since?` (default: last 30 days) |
| `get_recent_mentions` | `providerId?`, `count?` (default 20), `since?` — mentions **and replies** |
| `get_follower_insights` | `providerId?`, `count?` (default 10), `since?` (default: last 30 days) |
| `compare_platforms` | `since?` (default: last 7 days) |
| `get_unread_notifications` | `providerId?` |
| `get_provider_status` | none |

`providerId` works as it does for the A2A skills: omit it (or pass `all`) for every provider in one
call. An unknown one is an error listing the valid ids. A `since` that isn't an ISO-8601 timestamp
is asked about once, for clients that support MRTR input requests; otherwise the tool uses its
default window and says so. `get_recent_mentions` also carries an MCP Apps card
(`ui://socialagent/mentions.html`) for hosts that render one.

**Resources**, each JSON with a `marker` that changes exactly when the content does:

| URI | Content |
|---|---|
| `social://providers` | Each provider and whether it is connected |
| `social://mentions` | The 50 newest mentions and replies, every provider |
| `social://mentions/{providerId}` | The same for one provider, e.g. `social://mentions/bluesky` |
| `social://notifications/unread` | Every unread notification |
| `social://posts/{providerId}/{postId}` | One post's engagement counts (`postId` percent-escaped) |

Clients on `2026-07-28` can watch these with `subscriptions/listen` and get a
`notifications/resources/updated` when the polling loop stores something that changes them, so
updates arrive no faster than `SocialAgent:PollingIntervalMinutes`. Each stream ends after
`SocialAgent:Mcp:Subscriptions:StreamMinutes` (default 25) and the client reconnects. The change
feed is in-process, so the deployment must stay at **one replica**.

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
