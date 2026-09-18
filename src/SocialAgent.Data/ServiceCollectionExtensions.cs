using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SocialAgent.Data.Repositories;

namespace SocialAgent.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Assembly holding the SQLite migrations. EF cannot resolve two providers' migrations from one
    /// assembly, so each dialect gets its own; the names are used rather than a compile-time
    /// reference to keep SocialAgent.Data free of a dependency on its own migration assemblies.
    /// </summary>
    public const string SqliteMigrationsAssembly = "SocialAgent.Data.Migrations.Sqlite";

    /// <summary>Assembly holding the PostgreSQL migrations.</summary>
    public const string NpgsqlMigrationsAssembly = "SocialAgent.Data.Migrations.Npgsql";

    public static IServiceCollection AddSocialAgentData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("SocialAgent");
        var provider = configuration.GetSection("SocialAgent:DatabaseProvider").Value ?? "Sqlite";

        services.AddDbContext<SocialAgentDbContext>(options =>
        {
            if (provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
            {
                options.UseNpgsql(connectionString,
                    npgsql => npgsql.MigrationsAssembly(NpgsqlMigrationsAssembly));
            }
            else
            {
                options.UseSqlite(connectionString ?? "Data Source=socialagent.db",
                    sqlite => sqlite.MigrationsAssembly(SqliteMigrationsAssembly));
            }
        });

        services.AddScoped<ISocialDataRepository, SocialDataRepository>();
        return services;
    }
}
