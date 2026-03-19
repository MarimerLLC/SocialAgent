using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SocialAgent.Core.Providers;

namespace SocialAgent.Providers.Bluesky;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBlueskyProvider(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("SocialAgent:Providers:Bluesky");
        services.Configure<BlueskyOptions>(section);

        var options = new BlueskyOptions();
        section.Bind(options);

        if (!options.Enabled) return services;

        services.AddHttpClient<BlueskyProvider>(client =>
        {
            client.BaseAddress = new Uri(options.ServiceUrl);
        });
        services.AddSingleton<ISocialMediaProvider>(sp => sp.GetRequiredService<BlueskyProvider>());

        return services;
    }
}
