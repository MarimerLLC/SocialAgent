using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace SocialAgent.Host.Auth;

public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    private const string ApiKeyHeaderName = "X-Api-Key";
    private const string AuthorizationPrefix = "ApiKey ";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configuredKey = Options.ApiKey;
        if (string.IsNullOrEmpty(configuredKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("API key is not configured on the server."));
        }

        // Check X-Api-Key header first, then Authorization: ApiKey <key>
        string? providedKey = null;

        if (Request.Headers.TryGetValue(ApiKeyHeaderName, out var apiKeyHeader))
        {
            // A repeated header joins into "a,b" via ToString(); take the single value or nothing,
            // so a duplicate header cannot be used to smuggle a second candidate key.
            providedKey = apiKeyHeader.Count == 1 ? apiKeyHeader[0] : null;
        }
        else if (Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var value = authHeader.Count == 1 ? authHeader[0] : null;
            if (value is not null && value.StartsWith(AuthorizationPrefix, StringComparison.OrdinalIgnoreCase))
            {
                providedKey = value[AuthorizationPrefix.Length..].Trim();
            }
        }

        if (string.IsNullOrEmpty(providedKey))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!FixedTimeEquals(providedKey, configuredKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var claims = new[] { new Claim(ClaimTypes.Name, "ApiKeyClient") };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>
    /// Compares in time independent of how far the two values match, so response latency does not
    /// leak a prefix of the configured key. <see cref="CryptographicOperations.FixedTimeEquals"/>
    /// still short-circuits on length, which reveals only the key's length.
    /// </summary>
    private static bool FixedTimeEquals(string provided, string configured) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(configured));
}

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public string ApiKey { get; set; } = string.Empty;
}
