using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SocialAgent.Core.Providers;

namespace SocialAgent.Providers.Bluesky;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBlueskyProvider(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("SocialAgent:Providers:Bluesky");
        var optionsBuilder = services.AddOptions<BlueskyOptions>().Bind(section);

        var options = new BlueskyOptions();
        section.Bind(options);

        if (!options.Enabled) return services;

        // Fail at startup rather than on the first poll, where a bad config shows up only as a
        // recurring background log line.
        optionsBuilder
            .Validate(o => !string.IsNullOrWhiteSpace(o.ServiceUrl),
                "SocialAgent:Providers:Bluesky:ServiceUrl is required when the provider is enabled.")
            .Validate(o => Uri.TryCreate(o.ServiceUrl, UriKind.Absolute, out _),
                "SocialAgent:Providers:Bluesky:ServiceUrl must be an absolute URL.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Handle),
                "SocialAgent:Providers:Bluesky:Handle is required when the provider is enabled.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.AppPassword),
                "SocialAgent:Providers:Bluesky:AppPassword is required when the provider is enabled.")
            .ValidateOnStart();

        services.AddHttpClient(BlueskyProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(options.ServiceUrl);
            // The default 100s would stall a whole poll cycle behind one unresponsive PDS.
            client.Timeout = TimeSpan.FromSeconds(30);
        }).AddStandardResilienceHandler();

        // Singleton so one session (and its refresh JWT) is shared by the polling loop and the
        // A2A request path. The provider resolves a pooled HttpClient per call, so handler
        // rotation still happens normally.
        services.AddSingleton<BlueskyProvider>();
        services.AddSingleton<ISocialMediaProvider>(sp => sp.GetRequiredService<BlueskyProvider>());

        return services;
    }
}
