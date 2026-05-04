# Threads Provider

`SocialAgent.Providers.Threads` connects SocialAgent to Meta's Threads
platform via the public Threads API
([developers.facebook.com/docs/threads](https://developers.facebook.com/docs/threads)).

## Endpoint mapping

| `ISocialMediaProvider` method | Threads endpoint(s) |
|---|---|
| `ValidateConnectionAsync` | `GET /v1.0/me?fields=id` |
| `GetProfileAsync` | `GET /v1.0/me?fields=id,username,name,threads_profile_picture_url,threads_biography` plus optional `GET /v1.0/{user-id}/insights?metric=followers_count` |
| `GetRecentPostsAsync` | `GET /v1.0/me/threads?fields=id,text,timestamp,permalink,replies_count,reposts_count,quotes_count,media_type,media_url,is_quote_post,username&since={iso}` plus optional per-thread `GET /v1.0/{thread-id}/insights?metric=likes` |
| `GetNotificationsAsync` | merge of `GET /v1.0/me/mentions` and `GET /v1.0/me/replies`, deduped by id |
| `RefreshTokenAsync` | `GET /refresh_access_token?grant_type=th_refresh_token&access_token={current}` |

## Configuration

```jsonc
{
  "SocialAgent": {
    "Providers": {
      "Threads": {
        "Enabled": true,
        "BaseUrl": "https://graph.threads.net",
        "AccessToken": "<long-lived token>",
        "IncludePostInsights": false,
        "RefreshThresholdDays": 7,
        "RefreshCheckIntervalHours": 24
      }
    }
  }
}
```

| Key | Default | Notes |
|---|---|---|
| `Enabled` | `false` | When `false`, neither the provider nor the refresh service is registered. |
| `BaseUrl` | `https://graph.threads.net` | Override only if Meta moves the endpoint. |
| `AccessToken` | `""` | Long-lived token (60-day lifetime). On first run it is persisted to the database; subsequent rotations are written back. |
| `IncludePostInsights` | `false` | When `true`, the provider issues an extra `insights` call per post (likes) and one per profile fetch (follower count). Costs N+1 requests per poll cycle and requires `threads_manage_insights`. |
| `RefreshThresholdDays` | `7` | Trigger a refresh when fewer than this many days remain. Must be ≥ 1. |
| `RefreshCheckIntervalHours` | `24` | How often `ThreadsTokenRefreshService` checks the expiry. Must be ≥ 1. |

### Local development with user-secrets

```bash
cd src/SocialAgent.Host
dotnet user-secrets set "SocialAgent:Providers:Threads:Enabled" "true"
dotnet user-secrets set "SocialAgent:Providers:Threads:AccessToken" "<your token>"
```

## OAuth scopes

| Scope | Required for |
|---|---|
| `threads_basic` | `GET /me`, `GET /me/threads` (always required) |
| `threads_read_replies` | `GET /me/replies` |
| `threads_manage_replies` | `GET /me/mentions` |
| `threads_manage_insights` | `*/insights` (likes per post, follower count) — gated by `IncludePostInsights` |

`threads_manage_replies` and `threads_manage_insights` require Meta App
Review for production tokens. Until review completes you can still run
the provider; the affected endpoints will fail and the provider will log
a warning and return an empty list. Posts and profile metadata still
work with `threads_basic` alone.

## Token refresh

Threads long-lived tokens last **60 days** and can be refreshed any time
after they are at least 24 hours old. The
`ThreadsTokenRefreshService` background service:

1. On startup, loads the persisted token from the `ProviderTokens` table.
   If none exists, it seeds the table from `ThreadsOptions.AccessToken`
   with an assumed 60-day expiry.
2. Every `RefreshCheckIntervalHours` (default 24h), checks the expiry. If
   the remaining lifetime is less than `RefreshThresholdDays` (default
   7d), it calls `ThreadsProvider.RefreshTokenAsync()`, which issues
   `GET /refresh_access_token`, updates the in-memory `ThreadsTokenStore`,
   and returns the new `(token, expiresAt)`. The service then persists
   the new value via `ISocialDataRepository.UpsertProviderTokenAsync`.

If a refresh fails (network, scope error, expired token), the existing
token continues to be used until the next check interval. The service
logs the failure but does not crash the process.

## Known limitations

- **No native read marker.** Threads does not expose a notification
  marker like Mastodon or a per-notification read flag like Bluesky.
  All notifications returned by the provider have `IsRead = false`.
  Local read-state tracking is a candidate for a future release.
- **Follower count requires insights.** When `IncludePostInsights = false`
  (the default), `SocialProfile.FollowerCount` is `0`. Set the flag to
  `true` and grant `threads_manage_insights` to populate it.
- **`PostCount` and `FollowingCount` are always `0`.** The Threads API
  does not expose these on the user object.
- **Repost vs. quote counts are merged.** Threads tracks `reposts_count`
  and `quotes_count` separately; the provider sums them into
  `SocialPost.RepostCount` to fit the normalized model.
- **`IncludePostInsights` is N+1.** Enabling insights adds one extra HTTP
  call per post on each poll cycle. Watch your shared Graph API rate-limit
  budget — Threads insights share the same per-user pool as the rest of
  Meta's Graph API.

## Database schema change

Version 1.4.0 adds a `ProviderTokens` table that stores the rotating
Threads access token. The host creates the table automatically on
startup via `DatabaseMigrationService` — both fresh deployments
(through `EnsureCreatedAsync`) and existing PostgreSQL/SQLite deployments
(through an idempotent `CREATE TABLE IF NOT EXISTS` patch). No manual
DDL is required.

For reference, the patch DDL emitted by the host is:

```sql
-- PostgreSQL
CREATE TABLE IF NOT EXISTS "ProviderTokens" (
    "ProviderId"  text NOT NULL PRIMARY KEY,
    "AccessToken" text NOT NULL,
    "ExpiresAt"   timestamptz NOT NULL,
    "UpdatedAt"   timestamptz NOT NULL
);

-- SQLite (uses TEXT for DateTimeOffset, matching EF Core defaults)
CREATE TABLE IF NOT EXISTS "ProviderTokens" (
    "ProviderId"  TEXT NOT NULL PRIMARY KEY,
    "AccessToken" TEXT NOT NULL,
    "ExpiresAt"   TEXT NOT NULL,
    "UpdatedAt"   TEXT NOT NULL
);
```

This ad-hoc patch path is an interim approach. Future schema changes
will warrant moving to proper EF Core migrations.

## Files

- `src/SocialAgent.Providers.Threads/ThreadsProvider.cs` —
  `ISocialMediaProvider` implementation
- `src/SocialAgent.Providers.Threads/ThreadsTokenStore.cs` — singleton
  holding the current `(token, expiresAt)` in memory
- `src/SocialAgent.Providers.Threads/ThreadsOptions.cs` — configuration
- `src/SocialAgent.Host/Services/ThreadsTokenRefreshService.cs` —
  background service that owns refresh + persistence
- `src/SocialAgent.Core/Models/ProviderToken.cs` — persisted token row
