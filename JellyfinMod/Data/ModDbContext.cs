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

    /// <summary>Gets the files an administrator kept while other versions of the same title may go (P10).</summary>
    public DbSet<VersionKeep> VersionKeeps => Set<VersionKeep>();

    /// <summary>Gets full reconciliation run summaries.</summary>
    public DbSet<ReconciliationRun> ReconciliationRuns => Set<ReconciliationRun>();

    /// <summary>Gets durable per-user completion and protection evidence.</summary>
    public DbSet<CompletionObservation> CompletionObservations => Set<CompletionObservation>();

    /// <summary>Gets persisted retention policy revisions.</summary>
    public DbSet<RetentionPolicySnapshot> RetentionPolicySnapshots => Set<RetentionPolicySnapshot>();

    /// <summary>Gets durable access-aware completion policy results.</summary>
    public DbSet<RetentionEvaluation> RetentionEvaluations => Set<RetentionEvaluation>();

    /// <summary>Gets durable physical reclamation intents and outcomes.</summary>
    public DbSet<RetentionOperation> RetentionOperations => Set<RetentionOperation>();

    /// <summary>Gets durable scheduled/manual retention run summaries.</summary>
    public DbSet<RetentionRun> RetentionRuns => Set<RetentionRun>();

    /// <summary>Gets bound native episodes whose provider identity disagrees with their tracked episode.</summary>
    public DbSet<EpisodeConflict> EpisodeConflicts => Set<EpisodeConflict>();

    /// <summary>Gets the configured Torznab indexers (P4.A2).</summary>
    public DbSet<AcquisitionIndexer> AcquisitionIndexers => Set<AcquisitionIndexer>();

    /// <summary>Gets the configured download clients (P4.A2).</summary>
    public DbSet<AcquisitionDownloadClient> AcquisitionDownloadClients => Set<AcquisitionDownloadClient>();

    /// <summary>Gets the quality profiles (P4.A2).</summary>
    public DbSet<AcquisitionQualityProfile> AcquisitionQualityProfiles => Set<AcquisitionQualityProfile>();

    /// <summary>Gets the singleton acquisition settings (P4.A2).</summary>
    public DbSet<AcquisitionSettings> AcquisitionSettings => Set<AcquisitionSettings>();

    /// <summary>Gets the Prowlarr sources whose indexers are synced (P7.S9).</summary>
    public DbSet<ProwlarrSource> ProwlarrSources => Set<ProwlarrSource>();

    /// <summary>Gets durable manual grab operations (P4.A5).</summary>
    public DbSet<GrabOperation> GrabOperations => Set<GrabOperation>();

    /// <summary>Gets durable imports of accepted grabs (P5.I2).</summary>
    public DbSet<ImportOperation> ImportOperations => Set<ImportOperation>();

    /// <summary>Gets the plugin's ownership of seeding copies after import (P5.I6).</summary>
    public DbSet<SeedReleaseOperation> SeedReleaseOperations => Set<SeedReleaseOperation>();

    /// <summary>Gets ordered client-to-local path mappings (P5.I2).</summary>
    public DbSet<DownloadClientPathMapping> DownloadClientPathMappings => Set<DownloadClientPathMapping>();

    /// <summary>Gets releases an administrator blocked (P5.I7).</summary>
    public DbSet<ReleaseBlocklistEntry> ReleaseBlocklist => Set<ReleaseBlocklistEntry>();

    /// <summary>Gets per-target automation schedules (P6.M2).</summary>
    public DbSet<AutomationTargetState> AutomationTargets => Set<AutomationTargetState>();

    /// <summary>Gets the automation decision log (P6.M2).</summary>
    public DbSet<AutomationDecision> AutomationDecisions => Set<AutomationDecision>();

    /// <summary>Gets automation run summaries (P6.M2).</summary>
    public DbSet<AutomationRun> AutomationRuns => Set<AutomationRun>();

    /// <summary>Gets per-indexer budgets and breakers (P6.M2).</summary>
    public DbSet<IndexerBudgetState> IndexerBudgets => Set<IndexerBudgetState>();

    /// <summary>Gets upgrade operations (P6.M5).</summary>
    public DbSet<UpgradeOperation> UpgradeOperations => Set<UpgradeOperation>();

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
            // TMDB is an episode's identity when it has one; an episode known only from a TVDB-scraped library is
            // identified by its position instead (P10.E1). Either way one row per identity.
            e.HasIndex(x => new { x.EntryId, x.TmdbId }).IsUnique().HasFilter("\"TmdbId\" <> 0");
            e.HasIndex(x => new { x.EntryId, x.SeasonNumber, x.EpisodeNumber }, "IX_Episodes_EntryId_Position")
                .IsUnique().HasFilter("\"TmdbId\" = 0");
            e.Ignore(x => x.IsPositionIdentity);
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

        b.Entity<VersionKeep>(e =>
        {
            e.HasIndex(x => x.MediaPath).IsUnique();
            e.HasIndex(x => x.PhysicalIdentity);
            e.HasIndex(x => x.EntryId);
            e.HasOne<Entry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Episode>().WithMany().HasForeignKey(x => x.EpisodeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EpisodeConflict>(e =>
        {
            e.HasIndex(x => x.JellyfinItemId).IsUnique();
            e.HasIndex(x => x.EntryId);
            e.HasOne<Entry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Episode>().WithMany().HasForeignKey(x => x.EpisodeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ReconciliationRun>(e =>
        {
            e.Property(x => x.Status).HasMaxLength(16);
            e.HasIndex(x => x.StartedAt);
        });

        b.Entity<CompletionObservation>(e =>
        {
            e.HasIndex(x => new { x.TargetId, x.UserId }).IsUnique();
            e.HasIndex(x => x.JellyfinItemId);
            e.HasOne<Entry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RetentionPolicySnapshot>(e => e.HasKey(x => x.Id));

        b.Entity<RetentionEvaluation>(e =>
        {
            e.HasIndex(x => x.TargetId).IsUnique();
            e.HasIndex(x => x.Deadline);
            e.HasOne<Entry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Episode>().WithMany().HasForeignKey(x => x.EpisodeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RetentionOperation>(e =>
        {
            e.HasIndex(x => x.ActionId);
            e.HasIndex(x => new { x.BindingId, x.State });
            e.HasIndex(x => x.PhysicalIdentity);
            e.HasIndex(x => x.PreparedAt);
            // Reclamation operations are the audit trail of deleted media; removing an entry or episode
            // must detach them, never cascade them away (P3.T10).
            e.HasOne<Entry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Episode>().WithMany().HasForeignKey(x => x.EpisodeId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<RetentionRun>(e =>
        {
            e.Property(x => x.Status).HasMaxLength(16);
            e.HasIndex(x => x.StartedAt);
        });

        b.Entity<HistoryRecord>(e => e.HasIndex(x => x.EntryId));

        b.Entity<AcquisitionIndexer>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            // A source's synced indexers go with it; a manual indexer has no source.
            e.HasOne<ProwlarrSource>().WithMany().HasForeignKey(x => x.ProwlarrSourceId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ProwlarrSource>(e => e.HasIndex(x => x.Name).IsUnique());
        b.Entity<AcquisitionDownloadClient>(e => e.HasIndex(x => x.Name).IsUnique());
        b.Entity<AcquisitionQualityProfile>(e => e.HasIndex(x => x.Name).IsUnique());
        b.Entity<AcquisitionSettings>(e =>
        {
            // A selected client or default profile cannot disappear underneath the settings (P4.A2).
            e.HasOne<AcquisitionDownloadClient>().WithMany().HasForeignKey(x => x.DownloadClientId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AcquisitionQualityProfile>().WithMany().HasForeignKey(x => x.DefaultQualityProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        // Validated in code, not by a foreign key: adding one to Entries would make SQLite rebuild the catalog table
        // during the upgrade. Profile deletion refuses while an entry still uses the profile (P4.A2).
        b.Entity<Entry>(e => e.HasIndex(x => x.QualityProfileId));
        b.Entity<GrabOperation>(e =>
        {
            e.Property(x => x.State).HasMaxLength(16);
            e.HasIndex(x => new { x.RequestedBy, x.IdempotencyKey }).IsUnique();
            // Durable single ownership of a target and of a client/hash while an operation is active (P4.A5).
            e.HasIndex(x => x.ActiveTarget).IsUnique();
            e.HasIndex(x => x.ActiveHash).IsUnique();
            e.HasIndex(x => x.State);
            e.HasIndex(x => x.EntryId);
            // Grab operations are the audit of client handoffs; an entry removal detaches, never erases them.
            e.HasOne<Entry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Episode>().WithMany().HasForeignKey(x => x.EpisodeId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<ImportOperation>(e =>
        {
            e.Property(x => x.State).HasMaxLength(16);
            // One open import per grab; terminal operations release the key (P5.I2).
            e.HasIndex(x => x.OpenGrabKey).IsUnique();
            e.HasIndex(x => x.GrabId);
            e.HasIndex(x => x.State);
            e.HasIndex(x => x.EntryId);
            e.HasIndex(x => x.InfoHash);
            // Imports are the audit of library files the plugin created; an entry removal detaches them.
            e.HasOne<Entry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Episode>().WithMany().HasForeignKey(x => x.EpisodeId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<GrabOperation>().WithMany().HasForeignKey(x => x.GrabId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<SeedReleaseOperation>(e =>
        {
            e.Property(x => x.State).HasMaxLength(16);
            e.HasIndex(x => x.ImportOperationId).IsUnique();
            e.HasIndex(x => x.State);
            e.HasIndex(x => x.SeedingPhysicalIdentity);
            e.HasOne<ImportOperation>().WithMany().HasForeignKey(x => x.ImportOperationId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<DownloadClientPathMapping>(e =>
        {
            e.HasIndex(x => new { x.DownloadClientId, x.Order }).IsUnique();
            e.HasOne<AcquisitionDownloadClient>().WithMany().HasForeignKey(x => x.DownloadClientId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ReleaseBlocklistEntry>(e =>
        {
            e.HasIndex(x => x.InfoHash);
            e.HasIndex(x => new { x.IndexerId, x.SourceGuid });
        });
        b.Entity<AutomationTargetState>(e =>
        {
            e.HasIndex(x => x.NextSearchAt);
            e.HasIndex(x => x.EntryId);
            e.HasOne<Entry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AutomationDecision>(e =>
        {
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.EntryId);
            e.HasIndex(x => x.RunId);
        });
        b.Entity<AutomationRun>(e =>
        {
            e.Property(x => x.Status).HasMaxLength(16);
            e.HasIndex(x => x.StartedAt);
        });
        b.Entity<IndexerBudgetState>(e =>
            e.HasOne<AcquisitionIndexer>().WithMany().HasForeignKey(x => x.IndexerId).OnDelete(DeleteBehavior.Cascade));
        b.Entity<UpgradeOperation>(e =>
        {
            e.HasIndex(x => x.OpenTargetKey).IsUnique();
            e.HasIndex(x => x.State);
            e.HasIndex(x => x.NewGrabId);
            e.HasOne<Entry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<GrabOperation>(e => e.HasIndex(x => new { x.Automatic, x.CreatedAt }));
    }
}
