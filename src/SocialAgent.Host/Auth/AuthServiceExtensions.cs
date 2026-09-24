using Microsoft.AspNetCore.Authentication;

namespace SocialAgent.Host.Auth;

public static class AuthServiceExtensions
{
    public static IServiceCollection AddApiKeyAuthentication(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var apiKey = configuration["Authentication:ApiKey"] ?? string.Empty;

        // Outside Development the A2A endpoints require this key. Without it the agent would start,
        // report healthy, and reject every request — so refuse to start instead.
        if (!environment.IsDevelopment() && string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "Authentication:ApiKey must be configured outside the Development environment. " +
                "Set it via the Authentication__ApiKey environment variable or the social-agent-secrets Secret.");
        }

        services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName,
                options => options.ApiKey = apiKey);

        services.AddAuthorization();

        return services;
    }
}
