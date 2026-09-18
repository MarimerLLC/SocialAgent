using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SocialAgent.Core.Models;
using SocialAgent.Data;
using SocialAgent.Host.Services;

namespace SocialAgent.Host.Tests;

[TestClass]
public class DatabaseMigrationServiceTests
{
    private string _dbPath = null!;
    private string _connectionString = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"socialagent-migrate-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";
    }

    [TestCleanup]
    public void Cleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-shm", _dbPath + "-wal" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddDbContext<SocialAgentDbContext>(options =>
            options.UseSqlite(_connectionString,
                sqlite => sqlite.MigrationsAssembly(ServiceCollectionExtensions.SqliteMigrationsAssembly)));
        return services.BuildServiceProvider();
    }

    private static DatabaseMigrationService CreateService(ServiceProvider services) =>
        new(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DatabaseMigrationService>.Instance);

    private SocialAgentDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<SocialAgentDbContext>()
            .UseSqlite(_connectionString,
                sqlite => sqlite.MigrationsAssembly(ServiceCollectionExtensions.SqliteMigrationsAssembly))
            .Options);

    [TestMethod]
    public async Task FreshDatabase_IsCreatedByMigrations()
    {
        await using var services = BuildServices();

        await CreateService(services).StartAsync(CancellationToken.None);

        await using var db = CreateContext();
        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.AreEqual(1, applied.Count(), "the baseline migration should be applied");
        Assert.AreEqual(0, (await db.Database.GetPendingMigrationsAsync()).Count());

        // The schema is usable.
        db.Posts.Add(NewPost("p1"));
        await db.SaveChangesAsync();
        Assert.AreEqual(1, await db.Posts.CountAsync());
    }

    [TestMethod]
    public async Task LegacyEnsureCreatedDatabase_IsAdopted_WithoutLosingData()
    {
        // Reproduce a pre-1.5.0 database: schema via EnsureCreated, no __EFMigrationsHistory.
        await using (var legacy = CreateContext())
        {
            await legacy.Database.EnsureCreatedAsync();
            legacy.Posts.Add(NewPost("existing"));
            legacy.ProviderTokens.Add(new ProviderToken
            {
                ProviderId = "threads",
                AccessToken = "token-1",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await legacy.SaveChangesAsync();
        }

        await using var services = BuildServices();
        await CreateService(services).StartAsync(CancellationToken.None);

        await using var db = CreateContext();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        Assert.AreEqual(1, applied.Count, "the baseline should be recorded as already applied");
        Assert.AreEqual(0, (await db.Database.GetPendingMigrationsAsync()).Count());

        Assert.AreEqual(1, await db.Posts.CountAsync(), "existing rows must survive adoption");
        Assert.AreEqual("existing", (await db.Posts.SingleAsync()).PlatformPostId);
        Assert.AreEqual("token-1", (await db.ProviderTokens.SingleAsync()).AccessToken);
    }

    [TestMethod]
    public async Task IsIdempotent_AcrossRestarts()
    {
        await using var services = BuildServices();

        await CreateService(services).StartAsync(CancellationToken.None);
        await CreateService(services).StartAsync(CancellationToken.None);
        await CreateService(services).StartAsync(CancellationToken.None);

        await using var db = CreateContext();
        Assert.AreEqual(1, (await db.Database.GetAppliedMigrationsAsync()).Count());
    }

    [TestMethod]
    public async Task EmptyDatabaseFile_IsNotMistakenForLegacy()
    {
        // An existing but empty database must go down the normal migrate path.
        await using (var empty = CreateContext())
        {
            await empty.Database.OpenConnectionAsync();
            await empty.Database.CloseConnectionAsync();
        }

        await using var services = BuildServices();
        await CreateService(services).StartAsync(CancellationToken.None);

        await using var db = CreateContext();
        Assert.AreEqual(1, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.AreEqual(0, await db.Posts.CountAsync());
    }

    private static SocialPost NewPost(string platformPostId) => new()
    {
        Id = $"test:{platformPostId}",
        ProviderId = "test",
        PlatformPostId = platformPostId,
        AuthorHandle = "me",
        Content = "hello",
        CreatedAt = DateTimeOffset.UtcNow,
        IsOwnPost = true
    };
}

/// <summary>
/// The PostgreSQL half of the baseline-adoption behaviour. Opt in by pointing
/// <c>SOCIALAGENT_TEST_POSTGRES</c> at a scratch database, e.g.
/// <c>Host=localhost;Database=socialagent_test;Username=postgres;Password=postgres</c>.
/// The database is dropped and recreated by each test, so never point it at real data.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class DatabaseMigrationServicePostgresTests
{
    private string _connectionString = null!;

    [TestInitialize]
    public void Setup()
    {
        var connectionString = Environment.GetEnvironmentVariable("SOCIALAGENT_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("Set SOCIALAGENT_TEST_POSTGRES to run the PostgreSQL migration tests.");
        }
        _connectionString = connectionString!;
    }

    private SocialAgentDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<SocialAgentDbContext>()
            .UseNpgsql(_connectionString,
                npgsql => npgsql.MigrationsAssembly(ServiceCollectionExtensions.NpgsqlMigrationsAssembly))
            .Options);

    private DatabaseMigrationService CreateService()
    {
        var services = new ServiceCollection();
        services.AddDbContext<SocialAgentDbContext>(options =>
            options.UseNpgsql(_connectionString,
                npgsql => npgsql.MigrationsAssembly(ServiceCollectionExtensions.NpgsqlMigrationsAssembly)));
        var provider = services.BuildServiceProvider();
        return new DatabaseMigrationService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DatabaseMigrationService>.Instance);
    }

    private async Task ResetAsync()
    {
        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
    }

    [TestMethod]
    public async Task FreshDatabase_IsCreatedByMigrations()
    {
        await ResetAsync();

        await CreateService().StartAsync(CancellationToken.None);

        await using var db = CreateContext();
        Assert.AreEqual(1, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.AreEqual(0, (await db.Database.GetPendingMigrationsAsync()).Count());
    }

    [TestMethod]
    public async Task LegacyEnsureCreatedDatabase_IsAdopted_WithoutLosingData()
    {
        await ResetAsync();
        await using (var legacy = CreateContext())
        {
            await legacy.Database.EnsureCreatedAsync();
            legacy.Posts.Add(new SocialPost
            {
                Id = "test:existing",
                ProviderId = "test",
                PlatformPostId = "existing",
                AuthorHandle = "me",
                Content = "hello",
                CreatedAt = DateTimeOffset.UtcNow,
                IsOwnPost = true
            });
            await legacy.SaveChangesAsync();
        }

        await CreateService().StartAsync(CancellationToken.None);

        await using var db = CreateContext();
        Assert.AreEqual(1, (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.AreEqual(0, (await db.Database.GetPendingMigrationsAsync()).Count());
        Assert.AreEqual(1, await db.Posts.CountAsync(), "existing rows must survive adoption");
    }

    [TestMethod]
    public async Task IsIdempotent_AcrossRestarts()
    {
        await ResetAsync();

        await CreateService().StartAsync(CancellationToken.None);
        await CreateService().StartAsync(CancellationToken.None);

        await using var db = CreateContext();
        Assert.AreEqual(1, (await db.Database.GetAppliedMigrationsAsync()).Count());
    }
}
