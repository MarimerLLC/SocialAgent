# Changelog

All notable changes to SocialAgent are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project
adheres to [Semantic Versioning](https://semver.org/).

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
