using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SocialAgent.Core.Providers;

namespace SocialAgent.Providers.Threads;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddThreadsProvider(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("SocialAgent:Providers:Threads");
        var optionsBuilder = services.AddOptions<ThreadsOptions>().Bind(section);

        var options = new ThreadsOptions();
        section.Bind(options);

        if (!options.Enabled) return services;

        // Fail at startup rather than on the first poll, where a bad config shows up only as a
        // recurring background log line. AccessToken is not required here: the refresh service
        // may seed a rotated token from the database instead.
        optionsBuilder
            .Validate(o => !string.IsNullOrWhiteSpace(o.BaseUrl),
                "SocialAgent:Providers:Threads:BaseUrl is required when the provider is enabled.")
            .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _),
                "SocialAgent:Providers:Threads:BaseUrl must be an absolute URL.")
            .Validate(o => o.RefreshThresholdDays > 0,
                "SocialAgent:Providers:Threads:RefreshThresholdDays must be greater than zero.")
            .Validate(o => o.RefreshCheckIntervalHours > 0,
                "SocialAgent:Providers:Threads:RefreshCheckIntervalHours must be greater than zero.")
            .ValidateOnStart();

        services.AddSingleton<ThreadsTokenStore>();

        services.AddHttpClient(ThreadsProvider.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            // The default 100s would stall a whole poll cycle behind one unresponsive endpoint.
            client.Timeout = TimeSpan.FromSeconds(30);
        }).AddStandardResilienceHandler();

        // Singleton so the provider shares the token store with the refresh service. The provider
        // resolves a pooled HttpClient per call, so handler rotation still happens normally.
        services.AddSingleton<ThreadsProvider>();
        services.AddSingleton<ISocialMediaProvider>(sp => sp.GetRequiredService<ThreadsProvider>());

        return services;
    }
}
