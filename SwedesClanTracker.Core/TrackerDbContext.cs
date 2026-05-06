using Microsoft.EntityFrameworkCore;

namespace SwedesClanTracker.Core;

public class TrackerDbContext(DbContextOptions<TrackerDbContext> options) : DbContext(options)
{
    public DbSet<Player> Players => Set<Player>();
    public DbSet<PlayerSnapshot> PlayerSnapshots => Set<PlayerSnapshot>();
    public DbSet<PromotionCandidate> PromotionCandidates => Set<PromotionCandidate>();
    public DbSet<LifecycleEvent> LifecycleEvents => Set<LifecycleEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Player>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Username).HasMaxLength(64).IsRequired();
            e.Property(x => x.CurrentRank).HasMaxLength(32).IsRequired();
            e.Property(x => x.EligibleRank).HasMaxLength(32).IsRequired();
        });
        modelBuilder.Entity<PromotionCandidate>(e =>
        {
            e.Property(x => x.Reason).HasMaxLength(2048).IsRequired();
        });
    }
}
