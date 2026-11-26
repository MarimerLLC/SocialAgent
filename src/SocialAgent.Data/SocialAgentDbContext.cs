using Microsoft.EntityFrameworkCore;
using SocialAgent.Core.Models;

namespace SocialAgent.Data;

public class SocialAgentDbContext(DbContextOptions<SocialAgentDbContext> options) : DbContext(options)
{
    public DbSet<SocialPost> Posts => Set<SocialPost>();
    public DbSet<SocialNotification> Notifications => Set<SocialNotification>();
    public DbSet<SocialProfile> Profiles => Set<SocialProfile>();
    public DbSet<PollState> PollStates => Set<PollState>();
    public DbSet<ProviderToken> ProviderTokens => Set<ProviderToken>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no date type: EF stores DateTimeOffset there as TEXT and cannot translate
        // comparisons or ORDER BY on it, so every `since` filter and newest-first query failed.
        // Storing it as UTC ticks makes both work in SQL, ordered by instant. PostgreSQL has a
        // native timestamptz, and the model is built per provider, so its model and migrations are
        // untouched by this.
        if (Database.IsSqlite())
        {
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksDateTimeOffsetConverter>();
            configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<UtcTicksDateTimeOffsetConverter>();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SocialPost>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProviderId, e.PlatformPostId }).IsUnique();
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => new { e.IsOwnPost, e.CreatedAt });
        });

        modelBuilder.Entity<SocialNotification>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProviderId, e.PlatformNotificationId }).IsUnique();
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.Type);
        });

        modelBuilder.Entity<SocialProfile>(entity =>
        {
            entity.HasKey(e => e.ProviderId);
        });

        modelBuilder.Entity<PollState>(entity =>
        {
            entity.HasKey(e => e.ProviderId);
        });

        modelBuilder.Entity<ProviderToken>(entity =>
        {
            entity.HasKey(e => e.ProviderId);
        });
    }
}
