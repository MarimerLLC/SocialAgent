using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using SocialAgent.Core.Models;
using SocialAgent.Core.Providers;
using SocialAgent.Data;
using SocialAgent.Host.Services;

namespace SocialAgent.Host.Tests.Mcp;

/// <summary>
/// The real host over a temporary SQLite file, with the real providers and background loops
/// removed. Two fake providers (<see cref="Bluesky"/>, <see cref="Mastodon"/>) stand in, and
/// <see cref="PollAsync"/> runs one polling cycle against them, as the loop would.
/// </summary>
internal sealed class SocialAgentApp : WebApplicationFactory<Program>
{
    public const string Origin = "http://localhost";
    public const string ApiKey = "test-api-key";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"socialagent-mcp-{Guid.NewGuid():N}.db");
    private readonly string _environment;
    private readonly (string Key, string Value)[] _settings;
    private SocialMediaPollingService? _polling;

    public SocialAgentApp(params (string Key, string Value)[] settings) : this(Environments.Development, settings) { }

    public SocialAgentApp(string environment, params (string Key, string Value)[] settings)
    {
        _environment = environment;
        _settings = settings;
    }

    public FakeProvider Bluesky { get; } = new("bluesky", "Bluesky");

    public FakeProvider Mastodon { get; } = new("mastodon", "Mastodon");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("SocialAgent:DatabaseProvider", "Sqlite");
        builder.UseSetting("ConnectionStrings:SocialAgent", $"Data Source={_dbPath}");
        builder.UseSetting("Authentication:ApiKey", ApiKey);
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            // Whatever the machine's configuration enables, no real platform is called.
            services.RemoveAll<ISocialMediaProvider>();
            services.AddSingleton<ISocialMediaProvider>(Bluesky);
            services.AddSingleton<ISocialMediaProvider>(Mastodon);

            // The tests poll by hand; keep only the migrations at startup.
            foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)
                         && d.ImplementationType != typeof(DatabaseMigrationService)).ToList())
            {
                services.Remove(hosted);
            }

            services.RemoveAll<IDbContextOptionsConfiguration<SocialAgentDbContext>>();
            services.RemoveAll<DbContextOptions<SocialAgentDbContext>>();
            services.AddDbContext<SocialAgentDbContext>(options => options.UseSqlite(
                $"Data Source={_dbPath}", sqlite => sqlite.MigrationsAssembly(ServiceCollectionExtensions.SqliteMigrationsAssembly)));
        });
    }

    /// <summary>One polling cycle across the fake providers, publishing changes as the loop does.</summary>
    public async Task PollAsync()
    {
        // One instance, as in the host: it remembers each provider's last outcome.
        _polling ??= ActivatorUtilities.CreateInstance<SocialMediaPollingService>(Services);
        await _polling.PollOnceAsync(CancellationToken.None);
    }

    /// <summary>An MCP client for <c>/mcp</c>.</summary>
    public async Task<McpClient> ConnectAsync(string? protocolVersion = null, McpClientHandlers? handlers = null,
        Dictionary<string, string>? headers = null)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(Origin + "/mcp"), AdditionalHeaders = headers },
            CreateClient());
        return await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = protocolVersion, Handlers = handlers ?? new() });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // A temp file; the OS cleans it up eventually.
        }
    }
}

/// <summary>A provider whose posts and notifications a test sets directly.</summary>
internal sealed class FakeProvider(string id, string name) : ISocialMediaProvider
{
    private readonly Lock _lock = new();
    private List<SocialPost> _posts = [];
    private List<SocialNotification> _notifications = [];

    public string ProviderId { get; } = id;

    public string ProviderName { get; } = name;

    public bool Connected { get; set; } = true;

    public void SetPosts(params SocialPost[] posts)
    {
        lock (_lock)
        {
            _posts = [.. posts];
        }
    }

    public void SetNotifications(params SocialNotification[] notifications)
    {
        lock (_lock)
        {
            _notifications = [.. notifications];
        }
    }

    public SocialNotification Notification(string id, string type, string from = "alice", bool isRead = false,
        DateTimeOffset? createdAt = null, string? content = "hello") => new()
    {
        Id = $"{ProviderId}:{id}",
        ProviderId = ProviderId,
        PlatformNotificationId = id,
        Type = type,
        FromHandle = from,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        Content = content,
        Url = $"https://{ProviderId}.test/{id}",
        IsRead = isRead
    };

    public SocialPost Post(string id, int likes = 0, int reposts = 0, int replies = 0, DateTimeOffset? createdAt = null) => new()
    {
        Id = $"{ProviderId}:{id}",
        ProviderId = ProviderId,
        PlatformPostId = id,
        AuthorHandle = "me",
        Content = $"post {id}",
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow.AddHours(-1),
        LikeCount = likes,
        RepostCount = reposts,
        ReplyCount = replies,
        IsOwnPost = true
    };

    public Task<bool> ValidateConnectionAsync(CancellationToken ct = default) => Task.FromResult(Connected);

    public Task<SocialProfile> GetProfileAsync(CancellationToken ct = default) =>
        Connected
            ? Task.FromResult(new SocialProfile { ProviderId = ProviderId, Handle = "me" })
            : throw new HttpRequestException($"{ProviderName} is down");

    public Task<IReadOnlyList<SocialPost>> GetRecentPostsAsync(DateTimeOffset? since = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            // Fresh copies, as a real fetch returns, so the repository's tracked rows aren't these objects.
            return Task.FromResult<IReadOnlyList<SocialPost>>([.. _posts.Select(Copy)]);
        }
    }

    public Task<IReadOnlyList<SocialNotification>> GetNotificationsAsync(DateTimeOffset? since = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<SocialNotification>>([.. _notifications.Select(Copy)]);
        }
    }

    private static SocialPost Copy(SocialPost p) => new()
    {
        Id = p.Id, ProviderId = p.ProviderId, PlatformPostId = p.PlatformPostId, AuthorHandle = p.AuthorHandle,
        Content = p.Content, CreatedAt = p.CreatedAt, Url = p.Url, LikeCount = p.LikeCount,
        RepostCount = p.RepostCount, ReplyCount = p.ReplyCount, IsOwnPost = p.IsOwnPost
    };

    private static SocialNotification Copy(SocialNotification n) => new()
    {
        Id = n.Id, ProviderId = n.ProviderId, PlatformNotificationId = n.PlatformNotificationId, Type = n.Type,
        FromHandle = n.FromHandle, CreatedAt = n.CreatedAt, RelatedPostId = n.RelatedPostId, Content = n.Content,
        Url = n.Url, IsRead = n.IsRead
    };
}
