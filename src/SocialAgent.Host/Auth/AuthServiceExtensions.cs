using Microsoft.AspNetCore.Authentication;

namespace SocialAgent.Host.Auth;

public static class AuthServiceExtensions
{
    public static IServiceCollection AddApiKeyAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName,
                options =>
                {
                    options.ApiKey = configuration["Authentication:ApiKey"] ?? string.Empty;
                });

        services.AddAuthorization();

        return services;
    }
}
