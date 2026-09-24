using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SocialAgent.Data.Migrations.Npgsql;

/// <summary>
/// Used only by <c>dotnet ef</c> when scaffolding migrations into this assembly. The running app
/// configures its own context in <c>AddSocialAgentData</c>. No server is contacted at design time.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SocialAgentDbContext>
{
    public SocialAgentDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SocialAgentDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=socialagent;Username=design;Password=design",
                npgsql => npgsql.MigrationsAssembly(typeof(DesignTimeDbContextFactory).Assembly.FullName))
            .Options;

        return new SocialAgentDbContext(options);
    }
}
