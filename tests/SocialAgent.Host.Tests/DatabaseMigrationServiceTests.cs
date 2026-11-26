using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
        Assert.AreEqual(db.Database.GetMigrations().Count(), applied.Count(), "every migration should be applied");
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
        Assert.AreEqual(db.Database.GetMigrations().Count(), applied.Count, "every migration should be recorded as applied");
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
        Assert.AreEqual(db.Database.GetMigrations().Count(), (await db.Database.GetAppliedMigrationsAsync()).Count());
    }

    [TestMethod]
    public async Task Version13Database_WithoutProviderTokens_GainsTheTable()
    {
        // Production's actual shape: a 1.3.x schema that predates ProviderTokens entirely.
        await using (var legacy = CreateContext())
        {
            await legacy.Database.EnsureCreatedAsync();
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE \"ProviderTokens\"");
            legacy.Posts.Add(NewPost("existing"));
            await legacy.SaveChangesAsync();
        }

        await using var services = BuildServices();
        await CreateService(services).StartAsync(CancellationToken.None);

        await using var db = CreateContext();
        Assert.AreEqual(0, (await db.Database.GetPendingMigrationsAsync()).Count());
        Assert.AreEqual(1, await db.Posts.CountAsync(), "existing rows must survive adoption");

        // The table must really exist, not merely be recorded as migrated.
        db.ProviderTokens.Add(new ProviderToken
        {
            ProviderId = "threads",
            AccessToken = "token-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        Assert.AreEqual(1, await db.ProviderTokens.CountAsync());
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
        Assert.AreEqual(db.Database.GetMigrations().Count(), (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.AreEqual(0, await db.Posts.CountAsync());
    }

    // The TEXT form EF Core's SQLite provider wrote DateTimeOffset in before 1.5.4.
    private const string LegacyTextFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz";

    // One value per shape the conversion must handle: a three-digit fraction at UTC, a full
    // seven-digit fraction at a negative offset, no fraction at all (no '.') at a half-hour offset,
    // and a short fraction at a 45-minute offset.
    private static readonly DateTimeOffset UtcWithMillis = new DateTimeOffset(2026, 11, 25, 3, 6, 9, TimeSpan.Zero).AddTicks(4_620_000);
    private static readonly DateTimeOffset ChicagoFullFraction = new DateTimeOffset(2026, 11, 25, 9, 45, 0, TimeSpan.FromHours(-6)).AddTicks(1_234_567);
    private static readonly DateTimeOffset IndiaNoFraction = new(2026, 11, 25, 21, 15, 30, TimeSpan.FromHours(5.5));
    private static readonly DateTimeOffset NepalHalfSecond = new DateTimeOffset(2026, 1, 2, 0, 10, 0, TimeSpan.FromMinutes(345)).AddTicks(5_000_000);

    [TestMethod]
    public async Task TextDateTimeOffsets_AreConvertedToUtcTicks_WithoutLosingTheInstant()
    {
        // A 1.5.0–1.5.3 SQLite database: migrated up to AddProviderTokens, so its DateTimeOffset
        // columns are TEXT, holding rows in the format EF wrote them in.
        await using (var old = CreateContext())
        {
            await old.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync("AddProviderTokens");
            await InsertLegacyRowsAsync(old);
        }

        await using var services = BuildServices();
        await CreateService(services).StartAsync(CancellationToken.None);

        await using (var db = CreateContext())
        {
            Assert.AreEqual(0, (await db.Database.GetPendingMigrationsAsync()).Count());
            await AssertLegacyRowsReadBackAsync(db);

            // The queries issue #8 found broken now run, ordered and filtered by instant: India
            // (15:45:30 UTC) is 30 seconds after Chicago (15:45:00.1234567 UTC).
            var newestFirst = await db.Posts.OrderByDescending(p => p.CreatedAt).Select(p => p.PlatformPostId).ToListAsync();
            CollectionAssert.AreEqual(new[] { "india", "chicago", "utc", "nepal" }, newestFirst);
            var since = await db.Posts.Where(p => p.CreatedAt >= ChicagoFullFraction).Select(p => p.PlatformPostId).ToListAsync();
            CollectionAssert.AreEquivalent(new[] { "india", "chicago" }, since);
        }

        // Down restores TEXT, at offset +00:00 with the instant intact, and Up converts it again.
        await using (var db = CreateContext())
        {
            var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("AddProviderTokens");

            var text = await db.Database.SqlQueryRaw<string>("SELECT \"CreatedAt\" AS \"Value\" FROM \"Posts\" WHERE \"PlatformPostId\" = 'chicago'").SingleAsync();
            Assert.AreEqual(ChicagoFullFraction.ToUniversalTime().ToString(LegacyTextFormat), text);

            await migrator.MigrateAsync();
        }

        await using (var db = CreateContext())
        {
            await AssertLegacyRowsReadBackAsync(db);
        }
    }

    [TestMethod]
    public void LegacyTextFormat_MatchesWhatTheConversionSqlParses()
    {
        // Pin the shapes the migration's SQL relies on: 19 characters of local date and time, an
        // optional '.' and fraction, then a six-character "+HH:MM" offset.
        Assert.AreEqual("2026-11-25 03:06:09.462+00:00", UtcWithMillis.ToString(LegacyTextFormat));
        Assert.AreEqual("2026-11-25 09:45:00.1234567-06:00", ChicagoFullFraction.ToString(LegacyTextFormat));
        Assert.AreEqual("2026-11-25 21:15:30+05:30", IndiaNoFraction.ToString(LegacyTextFormat));
    }

    private static async Task InsertLegacyRowsAsync(SocialAgentDbContext db)
    {
        static string Text(DateTimeOffset value) => value.ToString(LegacyTextFormat);

        foreach (var (id, createdAt) in new[] { ("utc", UtcWithMillis), ("chicago", ChicagoFullFraction), ("india", IndiaNoFraction), ("nepal", NepalHalfSecond) })
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Posts" ("Id", "ProviderId", "PlatformPostId", "AuthorHandle", "Content", "CreatedAt",
                    "LikeCount", "RepostCount", "ReplyCount", "IsOwnPost", "LastUpdated")
                VALUES ({"test:" + id}, 'test', {id}, 'me', 'hello', {Text(createdAt)}, 0, 0, 0, 1, {Text(UtcWithMillis)})
                """);
        }

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Notifications" ("Id", "ProviderId", "PlatformNotificationId", "Type", "FromHandle", "CreatedAt", "IsRead")
            VALUES ('test:n1', 'test', 'n1', 'mention', 'someone', {Text(ChicagoFullFraction)}, 0)
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Profiles" ("ProviderId", "Handle", "FollowerCount", "FollowingCount", "PostCount", "LastUpdated")
            VALUES ('test', 'me', 1, 2, 3, {Text(IndiaNoFraction)})
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "PollStates" ("ProviderId", "LastPollTime") VALUES ('test', {Text(NepalHalfSecond)}), ('never', NULL)
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "ProviderTokens" ("ProviderId", "AccessToken", "ExpiresAt", "UpdatedAt")
            VALUES ('threads', 'token-1', {Text(IndiaNoFraction)}, {Text(ChicagoFullFraction)})
            """);
    }

    private static async Task AssertLegacyRowsReadBackAsync(SocialAgentDbContext db)
    {
        static void AssertInstant(DateTimeOffset expected, DateTimeOffset actual, string what)
        {
            Assert.AreEqual(expected.UtcTicks, actual.UtcTicks, $"{what}: the instant must survive to the tick");
            Assert.AreEqual(TimeSpan.Zero, actual.Offset, $"{what}: values read back as UTC");
        }

        var posts = await db.Posts.AsNoTracking().ToDictionaryAsync(p => p.PlatformPostId);
        Assert.AreEqual(4, posts.Count);
        AssertInstant(UtcWithMillis, posts["utc"].CreatedAt, "Posts.CreatedAt (utc)");
        AssertInstant(ChicagoFullFraction, posts["chicago"].CreatedAt, "Posts.CreatedAt (chicago)");
        AssertInstant(IndiaNoFraction, posts["india"].CreatedAt, "Posts.CreatedAt (india)");
        AssertInstant(NepalHalfSecond, posts["nepal"].CreatedAt, "Posts.CreatedAt (nepal)");
        AssertInstant(UtcWithMillis, posts["utc"].LastUpdated, "Posts.LastUpdated");

        AssertInstant(ChicagoFullFraction, (await db.Notifications.AsNoTracking().SingleAsync()).CreatedAt, "Notifications.CreatedAt");
        AssertInstant(IndiaNoFraction, (await db.Profiles.AsNoTracking().SingleAsync()).LastUpdated, "Profiles.LastUpdated");

        var polls = await db.PollStates.AsNoTracking().ToDictionaryAsync(p => p.ProviderId);
        AssertInstant(NepalHalfSecond, polls["test"].LastPollTime!.Value, "PollStates.LastPollTime");
        Assert.IsNull(polls["never"].LastPollTime, "a null stays null");

        var token = await db.ProviderTokens.AsNoTracking().SingleAsync();
        Assert.AreEqual("token-1", token.AccessToken);
        AssertInstant(IndiaNoFraction, token.ExpiresAt, "ProviderTokens.ExpiresAt");
        AssertInstant(ChicagoFullFraction, token.UpdatedAt, "ProviderTokens.UpdatedAt");
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
        Assert.AreEqual(db.Database.GetMigrations().Count(), (await db.Database.GetAppliedMigrationsAsync()).Count());
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
        Assert.AreEqual(db.Database.GetMigrations().Count(), (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.AreEqual(0, (await db.Database.GetPendingMigrationsAsync()).Count());
        Assert.AreEqual(1, await db.Posts.CountAsync(), "existing rows must survive adoption");
    }

    [TestMethod]
    public async Task Version13Database_WithoutProviderTokens_GainsTheTable()
    {
        // Production's actual shape: a 1.3.x schema that predates ProviderTokens entirely.
        await ResetAsync();
        await using (var legacy = CreateContext())
        {
            await legacy.Database.EnsureCreatedAsync();
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE \"ProviderTokens\"");
        }

        await CreateService().StartAsync(CancellationToken.None);

        await using var db = CreateContext();
        Assert.AreEqual(0, (await db.Database.GetPendingMigrationsAsync()).Count());
        db.ProviderTokens.Add(new ProviderToken
        {
            ProviderId = "threads",
            AccessToken = "token-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        Assert.AreEqual(1, await db.ProviderTokens.CountAsync(), "the missing table must actually be created");
    }

    [TestMethod]
    public async Task IsIdempotent_AcrossRestarts()
    {
        await ResetAsync();

        await CreateService().StartAsync(CancellationToken.None);
        await CreateService().StartAsync(CancellationToken.None);

        await using var db = CreateContext();
        Assert.AreEqual(db.Database.GetMigrations().Count(), (await db.Database.GetAppliedMigrationsAsync()).Count());
    }
}
