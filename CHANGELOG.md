# Changelog

All notable changes to SocialAgent are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project
adheres to [Semantic Versioning](https://semver.org/).

## [1.5.0] - 2026-09-18

### Fixed
- **The build was failing on `main`.** `Microsoft.EntityFrameworkCore.Sqlite` 10.0.5 pulled
  `SQLitePCLRaw.lib.e_sqlite3` 2.1.11, which carries a high-severity advisory
  (GHSA-2m69-gcr7-jv3q); combined with `TreatWarningsAsErrors`, NU1903 broke `dotnet build`
  for four projects. EF Core is now on 10.0.12, which brings SQLitePCLRaw 2.1.12.
- **The container image could not be built.** The Dockerfile never listed
  `SocialAgent.Providers.Threads.csproj` in its restore layer, added in 1.4.0. Restore skipped
  the project and `dotnet publish --no-restore` then failed with NETSDK1004.
- **Bluesky stopped working a couple of hours after every pod start.** The provider cached its
  access JWT forever and never used the refresh JWT it had already parsed, so once the token
  expired every call returned 401 until the pod restarted. Sessions are now refreshed via
  `com.atproto.server.refreshSession` on a 401, falling back to a full login, and the failed
  request is retried.
- **Providers pinned a single `HttpClient` for the process lifetime.** `AddHttpClient<T>`
  registers `T` as transient, so resolving it from a singleton factory captured one instance
  and its message handler forever — defeating handler rotation (stale DNS) and sharing a
  mutable `DefaultRequestHeaders` between the polling loop and A2A request threads, which
  `HttpHeaders` does not support. Providers now take `IHttpClientFactory`, resolve a pooled
  client per call, and set `Authorization` per request.
- **`/health/ready` reported healthy with the database down.** `AddHealthChecks()` had no checks
  registered. Readiness now runs `AddDbContextCheck`; liveness stays a pure process check so a
  database blip does not restart the pod.
- **A transient network error restarted the whole pod.** A request timeout surfaces as
  `TaskCanceledException`, which is an `OperationCanceledException`, and the background services
  filtered their handlers on that type — so a blip talking to Mastodon escaped `ExecuteAsync`
  and the host's default `StopHost` behaviour terminated the process. The running 1.3.4 pod had
  60 restarts from exactly this. The handlers now key off the stopping token instead, so real
  shutdown still propagates while timeouts are logged and polling continues. The same filter is
  corrected in the providers, where it defeated their graceful-degradation paths.
- **Polling silently dropped data.** Each provider fetched one fixed page and filtered `since`
  client-side, so anything beyond that page in a poll interval was lost. All three providers now
  page (Mastodon `max_id`, Bluesky `cursor`, Threads `after`) until they pass the cutoff.
- The agent card reported `1.4.0.0`; it now reports the three-part informational version.
- Bluesky posts recorded the relay's `indexedAt` as `CreatedAt`; the record's authoring
  timestamp is used when present.

### Security
- API keys are compared with `CryptographicOperations.FixedTimeEquals` instead of
  `string.Equals`, so response latency no longer leaks a prefix of the configured key. Repeated
  `X-Api-Key` headers are rejected rather than joined.
- The Threads access token is sent in an `Authorization` header rather than the query string,
  keeping it out of `url.full` on OpenTelemetry spans and out of exception messages. The
  documented query-parameter form remains as a fallback if Meta rejects the header.
- A missing `Authentication:ApiKey` outside Development now fails at startup instead of leaving
  the agent running and rejecting every request.
- The container runs as a non-root user with a read-only root filesystem, dropped capabilities
  and `RuntimeDefault` seccomp.

### Added
- **EF Core migrations** replace `EnsureCreatedAsync` plus hand-written
  `CREATE TABLE IF NOT EXISTS` patching, in `SocialAgent.Data.Migrations.Sqlite` and
  `SocialAgent.Data.Migrations.Npgsql` (EF cannot resolve two providers' migrations from one
  assembly). `DatabaseMigrationService` adopts a pre-1.5.0 database by recording the baseline as
  already applied — see the migration notes below.
- **CI** (`.github/workflows/ci.yml`) builds and tests the solution and builds the container
  image on every push and pull request. Nothing verified this repository before.
- `Microsoft.Extensions.Http.Resilience` standard handlers and a 30s timeout on every provider
  HTTP client, replacing the 100s default that let one unresponsive endpoint stall a poll cycle.
- Provider options are validated with `ValidateOnStart`, so an enabled-but-unconfigured provider
  fails at startup rather than logging on every poll.
- `SocialAgent.TestSupport` with a recording HTTP handler, plus test projects covering the host
  (skill dispatch, API-key auth, migrations) and the new provider HTTP behaviour. Integration
  tests now self-skip without credentials instead of failing.

### Changed
- Mastodon status and bio HTML is converted to plain text via `SocialAgent.Core.Text.HtmlText`
  instead of being stored and returned as raw markup.
- Mastodon caches the account id rather than calling `verify_credentials` on every poll.
- Post and notification upserts do one batched lookup per cycle instead of a `SELECT` per row.
- Engagement summaries and top-engager rankings aggregate in SQL rather than materialising the
  whole retention window in memory. `ISocialDataRepository` gains
  `GetPostEngagementTotalsAsync`, `GetNotificationCountsByTypeAsync` and `GetEngagerTalliesAsync`.
- `SkillRouter` is bounded by a configurable timeout (`LLM:Low:TimeoutSeconds`, default 5s),
  prefers an exact skill-id match, and no longer swallows caller cancellation.
- Agent version bumped from **1.4.0** to **1.5.0**.

### Migration notes
- The first 1.5.0 start adopts the existing database. Migrations mirror the real schema history:
  `InitialCreate` is the 1.3.x schema and `AddProviderTokens` is the table 1.4.0 introduced.
  `DatabaseMigrationService` records each migration whose table already exists as applied, then
  `MigrateAsync` creates anything genuinely missing — so a 1.3.x database (which production is)
  gains `ProviderTokens`, while a 1.4.0 database is left as it is. No existing rows are touched.
- Rehearsed against a restored copy of the production database (2026-09-21 backup, Postgres
  16.12): `InitialCreate` stamped, `AddProviderTokens` applied, all row counts unchanged, and
  skills answered from the upgraded data. **Take a fresh backup immediately before the rollout
  anyway.**
- `AspNetCore.HealthChecks.NpgSql` was removed from `Directory.Packages.props`; it was never
  referenced by any project.
- The Threads ConfigMap/Secret references in `deploy/k8s/deployment.yaml` are now `optional: true`.
  Threads is disabled and those keys are absent from the deployed ConfigMap and Secret; without
  this the pod would land in `CreateContainerConfigError`.

## [1.4.0] - 2026-05-04

### Added
- **Threads provider** (`SocialAgent.Providers.Threads`) implementing
  `ISocialMediaProvider` against Meta's Threads API. Maps `/v1.0/me`,
  `/v1.0/me/threads`, `/v1.0/me/mentions`, and `/v1.0/me/replies` to the
  standard provider methods. Notifications are synthesized from mentions
  plus replies.
- **Threads long-lived token refresh** via the new
  `ThreadsTokenRefreshService`. Calls
  `GET /refresh_access_token?grant_type=th_refresh_token` ahead of the
  configured `RefreshThresholdDays` (default 7) and persists the new token
  to the database so refreshes survive pod restarts.
- **`ProviderToken` entity** and repository methods
  (`GetProviderTokenAsync`, `UpsertProviderTokenAsync`) for storing
  rotating OAuth bearer tokens.
- New configuration section `SocialAgent:Providers:Threads` (`Enabled`,
  `BaseUrl`, `AccessToken`, `IncludePostInsights`, `RefreshThresholdDays`,
  `RefreshCheckIntervalHours`).
- Kubernetes ConfigMap / Secret / Deployment wiring for the Threads
  provider.
- `docs/providers/threads.md` long-form provider documentation covering
  OAuth scopes, token refresh, and known limitations.

### Changed
- Agent version bumped from **1.3.4** to **1.4.0**; the `/.well-known/agent-card.json`
  now reports `1.4.0`.
- Agent card description updated to mention Threads alongside Mastodon
  and Bluesky.

### Migration notes
- The new `ProviderTokens` table is created automatically by
  `DatabaseMigrationService` on every startup, both for fresh databases
  (via `EnsureCreatedAsync`) and existing PostgreSQL/SQLite databases
  (via an idempotent `CREATE TABLE IF NOT EXISTS` patch). No manual DDL
  is required when rolling out 1.4.0. See
  [`docs/providers/threads.md`](docs/providers/threads.md#database-schema-change)
  for the exact DDL the host runs.
- The `threads_manage_replies` and `threads_manage_insights` OAuth scopes
  require Meta App Review for production tokens. The provider degrades
  gracefully when scopes are missing: affected endpoints log a warning and
  return empty.

## Prior versions

For commits prior to 1.4.0, see `git log`. Notable changes include the
A2A 1.0 migration (#4), data retention service (#3), API key
authentication (#2), and the initial scaffold.
