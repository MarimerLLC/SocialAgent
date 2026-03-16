using Microsoft.EntityFrameworkCore;
using SocialAgent.Core.Models;

namespace SocialAgent.Data;

public class SocialAgentDbContext(DbContextOptions<SocialAgentDbContext> options) : DbContext(options)
{
    public DbSet<SocialPost> Posts => Set<SocialPost>();
    public DbSet<SocialNotification> Notifications => Set<SocialNotification>();
    public DbSet<SocialProfile> Profiles => Set<SocialProfile>();
    public DbSet<PollState> PollStates => Set<PollState>();

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
    }
}
