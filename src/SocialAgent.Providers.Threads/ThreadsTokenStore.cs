using Microsoft.Extensions.Options;

namespace SocialAgent.Providers.Threads;

public class ThreadsTokenStore(IOptions<ThreadsOptions> options)
{
    private readonly ThreadsOptions _options = options.Value;
    private readonly object _lock = new();
    private string? _token;
    private DateTimeOffset _expiresAt;

    public (string Token, DateTimeOffset ExpiresAt) Current
    {
        get
        {
            lock (_lock)
            {
                if (_token is null)
                {
                    // Lazy fallback so the provider can operate before the refresh service has
                    // had a chance to seed from the database. The 60-day default mirrors a fresh
                    // Threads long-lived token; the refresh service will tighten the expiry once
                    // it has the persisted value.
                    _token = _options.AccessToken;
                    _expiresAt = DateTimeOffset.UtcNow.AddDays(60);
                }
                if (string.IsNullOrEmpty(_token))
                {
                    throw new InvalidOperationException(
                        "Threads access token is not configured. Set SocialAgent:Providers:Threads:AccessToken.");
                }
                return (_token, _expiresAt);
            }
        }
    }

    public void Set(string token, DateTimeOffset expiresAt)
    {
        lock (_lock)
        {
            _token = token;
            _expiresAt = expiresAt;
        }
    }
}
