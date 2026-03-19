using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SocialAgent.Core.Providers;

namespace SocialAgent.Providers.Mastodon;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMastodonProvider(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("SocialAgent:Providers:Mastodon");
        services.Configure<MastodonOptions>(section);

        var options = new MastodonOptions();
        section.Bind(options);

        if (!options.Enabled) return services;

        services.AddHttpClient<MastodonProvider>(client =>
        {
            client.BaseAddress = new Uri(options.InstanceUrl);
        });
        services.AddSingleton<ISocialMediaProvider>(sp => sp.GetRequiredService<MastodonProvider>());

        return services;
    }
}
