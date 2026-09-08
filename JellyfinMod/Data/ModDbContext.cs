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

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Entry>(e =>
        {
            e.HasIndex(x => new { x.MediaType, x.TmdbId }).IsUnique();
            e.HasIndex(x => x.JellyfinItemId);
            e.HasIndex(x => x.State);
        });

        b.Entity<HistoryRecord>(e => e.HasIndex(x => x.EntryId));
    }
}
