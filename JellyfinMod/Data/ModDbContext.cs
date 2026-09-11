using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace JellyfinMod.Data;

/// <summary>The plugin's own SQLite database. Nothing here is a Jellyfin library item.</summary>
public class ModDbContext : DbContext
{
    private readonly string _dbPath;

    /// <summary>Initializes a new instance of the <see cref="ModDbContext"/> class.</summary>
    public ModDbContext(string dbPath) => _dbPath = dbPath;

    /// <summary>Gets or sets the catalog entries.</summary>
    public DbSet<Entry> Entries => Set<Entry>();

    /// <summary>Gets or sets the history records.</summary>
    public DbSet<HistoryRecord> History => Set<HistoryRecord>();

    /// <summary>Gets the individually tracked series episodes.</summary>
    public DbSet<Episode> Episodes => Set<Episode>();

    /// <summary>Gets the durable native title representations.</summary>
    public DbSet<EntryBinding> EntryBindings => Set<EntryBinding>();

    /// <summary>Gets the durable native episode representations.</summary>
    public DbSet<EpisodeBinding> EpisodeBindings => Set<EpisodeBinding>();

    /// <summary>Gets full reconciliation run summaries.</summary>
    public DbSet<ReconciliationRun> ReconciliationRuns => Set<ReconciliationRun>();

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Entry>(e =>
        {
            e.HasIndex(x => new { x.MediaType, x.TmdbId, x.TargetLibraryId }).IsUnique();
            e.HasIndex(x => x.JellyfinItemId);
            e.HasIndex(x => x.State);
        });

        b.Entity<Episode>(e =>
        {
            e.HasIndex(x => new { x.EntryId, x.SeasonNumber, x.EpisodeNumber });
            e.HasIndex(x => new { x.EntryId, x.TmdbId }).IsUnique();
            e.HasOne<Entry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EntryBinding>(e =>
        {
            e.HasIndex(x => x.JellyfinItemId).IsUnique();
            e.HasOne<Entry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EpisodeBinding>(e =>
        {
            e.HasIndex(x => x.JellyfinItemId).IsUnique();
            e.HasOne<Episode>().WithMany().HasForeignKey(x => x.EpisodeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ReconciliationRun>(e =>
        {
            e.Property(x => x.Status).HasMaxLength(16);
            e.HasIndex(x => x.StartedAt);
        });

        b.Entity<HistoryRecord>(e => e.HasIndex(x => x.EntryId));
    }
}
