using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SocialAgent.Core.Providers;

namespace SocialAgent.Providers.Mastodon;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMastodonProvider(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("SocialAgent:Providers:Mastodon");
        var optionsBuilder = services.AddOptions<MastodonOptions>().Bind(section);

        var options = new MastodonOptions();
        section.Bind(options);

        if (!options.Enabled) return services;

        // Fail at startup rather than on the first poll, where a bad config shows up only as a
        // recurring background log line.
        optionsBuilder
            .Validate(o => !string.IsNullOrWhiteSpace(o.InstanceUrl),
                "SocialAgent:Providers:Mastodon:InstanceUrl is required when the provider is enabled.")
            .Validate(o => Uri.TryCreate(o.InstanceUrl, UriKind.Absolute, out _),
                "SocialAgent:Providers:Mastodon:InstanceUrl must be an absolute URL.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.AccessToken),
                "SocialAgent:Providers:Mastodon:AccessToken is required when the provider is enabled.")
            .ValidateOnStart();

        services.AddHttpClient(MastodonProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(options.InstanceUrl);
            // The default 100s would stall a whole poll cycle behind one unresponsive instance.
            client.Timeout = TimeSpan.FromSeconds(30);
        }).AddStandardResilienceHandler();

        // Singleton so the cached account id survives between polls. The provider resolves a
        // pooled HttpClient per call, so handler rotation still happens normally.
        services.AddSingleton<MastodonProvider>();
        services.AddSingleton<ISocialMediaProvider>(sp => sp.GetRequiredService<MastodonProvider>());

        return services;
    }
}
