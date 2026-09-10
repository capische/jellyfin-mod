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
            e.HasIndex(x => new { x.EntryId, x.SeasonNumber, x.EpisodeNumber }).IsUnique();
            e.HasIndex(x => new { x.EntryId, x.TmdbId }).IsUnique();
            e.HasOne<Entry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<HistoryRecord>(e => e.HasIndex(x => x.EntryId));
    }
}
