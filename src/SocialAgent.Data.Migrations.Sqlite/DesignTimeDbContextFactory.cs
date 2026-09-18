using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SocialAgent.Data.Migrations.Sqlite;

/// <summary>
/// Used only by <c>dotnet ef</c> when scaffolding migrations into this assembly. The running app
/// configures its own context in <c>AddSocialAgentData</c>.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SocialAgentDbContext>
{
    public SocialAgentDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SocialAgentDbContext>()
            .UseSqlite(
                "Data Source=design-time.db",
                sqlite => sqlite.MigrationsAssembly(typeof(DesignTimeDbContextFactory).Assembly.FullName))
            .Options;

        return new SocialAgentDbContext(options);
    }
}
