using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SocialAgent.Core.Providers;

namespace SocialAgent.Providers.Threads;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddThreadsProvider(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("SocialAgent:Providers:Threads");
        services.Configure<ThreadsOptions>(section);

        var options = new ThreadsOptions();
        section.Bind(options);

        if (!options.Enabled) return services;

        services.AddSingleton<ThreadsTokenStore>();

        services.AddHttpClient<ThreadsProvider>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
        });
        services.AddSingleton<ISocialMediaProvider>(sp => sp.GetRequiredService<ThreadsProvider>());

        return services;
    }
}
