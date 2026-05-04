using Microsoft.Extensions.Options;
using SocialAgent.Core.Models;
using SocialAgent.Data.Repositories;
using SocialAgent.Providers.Threads;

namespace SocialAgent.Host.Services;

public class ThreadsTokenRefreshService(
    IServiceScopeFactory scopeFactory,
    ThreadsTokenStore tokenStore,
    IOptions<ThreadsOptions> options,
    ILogger<ThreadsTokenRefreshService> logger) : BackgroundService
{
    private const string ProviderId = "threads";
    private readonly ThreadsOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SeedTokenFromDatabaseAsync(stoppingToken);

        var interval = TimeSpan.FromHours(Math.Max(1, _options.RefreshCheckIntervalHours));
        var threshold = TimeSpan.FromDays(Math.Max(1, _options.RefreshThresholdDays));

        logger.LogInformation(
            "Threads token refresh service running; check every {Interval}h, refresh when within {Threshold}d of expiry",
            interval.TotalHours, threshold.TotalDays);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndRefreshAsync(threshold, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error during Threads token refresh check");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CheckAndRefreshAsync(TimeSpan threshold, CancellationToken ct)
    {
        DateTimeOffset expiresAt;
        try
        {
            (_, expiresAt) = tokenStore.Current;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Threads token unavailable; skipping refresh check");
            return;
        }

        var remaining = expiresAt - DateTimeOffset.UtcNow;
        if (remaining > threshold)
        {
            logger.LogDebug("Threads token has {Remaining} remaining; no refresh needed", remaining);
            return;
        }

        logger.LogInformation(
            "Threads token expires at {Expiry:o} ({Remaining} remaining); refreshing",
            expiresAt, remaining);

        using var scope = scopeFactory.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<ThreadsProvider>();
        var refreshed = await provider.RefreshTokenAsync(ct);
        if (refreshed is null) return;

        var repo = scope.ServiceProvider.GetRequiredService<ISocialDataRepository>();
        await repo.UpsertProviderTokenAsync(new ProviderToken
        {
            ProviderId = ProviderId,
            AccessToken = refreshed.Value.Token,
            ExpiresAt = refreshed.Value.ExpiresAt,
            UpdatedAt = DateTimeOffset.UtcNow
        }, ct);
    }

    private async Task SeedTokenFromDatabaseAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISocialDataRepository>();
            var stored = await repo.GetProviderTokenAsync(ProviderId, ct);

            if (stored is not null && !string.IsNullOrEmpty(stored.AccessToken))
            {
                tokenStore.Set(stored.AccessToken, stored.ExpiresAt);
                logger.LogInformation(
                    "Loaded persisted Threads token (expires {Expiry:o}, last updated {Updated:o})",
                    stored.ExpiresAt, stored.UpdatedAt);
                return;
            }

            if (string.IsNullOrEmpty(_options.AccessToken))
            {
                logger.LogWarning(
                    "Threads provider is enabled but no AccessToken is configured; provider calls will fail");
                return;
            }

            // First-run: persist the configured token with a default 60-day lifetime so the
            // refresh loop has an expiry to reason about.
            var seededExpiry = DateTimeOffset.UtcNow.AddDays(60);
            tokenStore.Set(_options.AccessToken, seededExpiry);
            await repo.UpsertProviderTokenAsync(new ProviderToken
            {
                ProviderId = ProviderId,
                AccessToken = _options.AccessToken,
                ExpiresAt = seededExpiry,
                UpdatedAt = DateTimeOffset.UtcNow
            }, ct);
            logger.LogInformation(
                "Seeded Threads token from configuration; assumed expiry {Expiry:o}",
                seededExpiry);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to seed Threads token from database");
        }
    }
}
