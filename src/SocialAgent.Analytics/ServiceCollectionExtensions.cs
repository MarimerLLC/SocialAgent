using Microsoft.Extensions.DependencyInjection;
using SocialAgent.Core.Analytics;

namespace SocialAgent.Analytics;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSocialAgentAnalytics(this IServiceCollection services)
    {
        services.AddScoped<IAnalyticsService, AnalyticsService>();
        return services;
    }
}
