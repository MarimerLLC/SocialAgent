using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SocialAgent.Data.Repositories;

namespace SocialAgent.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSocialAgentData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("SocialAgent");
        var provider = configuration.GetSection("SocialAgent:DatabaseProvider").Value ?? "Sqlite";

        services.AddDbContext<SocialAgentDbContext>(options =>
        {
            if (provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
            {
                options.UseNpgsql(connectionString);
            }
            else
            {
                options.UseSqlite(connectionString ?? "Data Source=socialagent.db");
            }
        });

        services.AddScoped<ISocialDataRepository, SocialDataRepository>();
        return services;
    }
}
