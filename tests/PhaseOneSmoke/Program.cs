using System.Net;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using JellyfinMod;
using JellyfinMod.Api;
using JellyfinMod.Api.Contracts;
using JellyfinMod.Data;
using JellyfinMod.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

var folder = Path.Combine(Path.GetTempPath(), "jfmod-phase-one-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var dbPath = Path.Combine(folder, "upgrade.db");
    var firstId = Guid.NewGuid();
    var firstNativeId = Guid.NewGuid();
    var libraryId = Guid.NewGuid();
    await using (var database = new ModDbContext(dbPath))
    {
        await database.GetService<IMigrator>().MigrateAsync("20260906021220_InitialCreate");
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Entries (Id, MediaType, TmdbId, Title, State, Monitored, AddedAt, TargetLibraryId, JellyfinItemId) VALUES ({firstId}, 'movie', 123, 'Existing title', 4, 1, {DateTime.UtcNow}, {libraryId}, {firstNativeId})");
        await database.GetService<IMigrator>().MigrateAsync("20260908011536_PhaseOneEntries");
        Assert(await database.Entries.Select(entry => entry.Id).SingleAsync() == firstId, "Upgrade preserves entries");
        var secondEntryId = Guid.NewGuid();
        var secondLibraryId = Guid.NewGuid();
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Entries (Id, MediaType, TmdbId, Title, State, Monitored, AddedAt, TargetLibraryId) VALUES ({secondEntryId}, 'movie', 123, 'Second library', 0, 1, {DateTime.UtcNow}, {secondLibraryId})");
        var seriesNativeId = Guid.NewGuid();
        var showId = Guid.NewGuid();
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Entries (Id, MediaType, TmdbId, Title, State, Monitored, AddedAt, TargetLibraryId, JellyfinItemId) VALUES ({showId}, 'series', 123, 'Series', 4, 1, {DateTime.UtcNow}, {libraryId}, {seriesNativeId})");
        var episodeNativeId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Episodes (Id, EntryId, TmdbId, SeasonNumber, EpisodeNumber, Title, Monitored, State, JellyfinItemId) VALUES ({episodeId}, {showId}, 999, 1, 1, 'Pilot', 1, 4, {episodeNativeId})");
        await database.Database.MigrateAsync();
        Assert(await database.Entries.CountAsync() == 3, "Identity includes media type and library");
        Assert((await database.EntryBindings.SingleAsync(binding => binding.EntryId == firstId)).JellyfinItemId == firstNativeId &&
            (await database.EpisodeBindings.SingleAsync()).JellyfinItemId == episodeNativeId &&
            (await database.EpisodeBindings.SingleAsync()).SeriesItemId == seriesNativeId &&
            (await database.EpisodeBindings.SingleAsync()).TargetLibraryId == libraryId,
            "Phase 2 migration preserves canonical title and episode bindings");
        database.Entries.Add(new Entry { MediaType = "movie", TmdbId = 123, TargetLibraryId = libraryId });
        await Throws<DbUpdateException>(() => database.SaveChangesAsync());
        database.ChangeTracker.Clear();
        Assert(await database.Entries.CountAsync() == 3, "Same-library duplicate rejected");
        database.Entries.Remove(await database.Entries.SingleAsync(e => e.Id == showId));
        await database.SaveChangesAsync();
        Assert(await database.Episodes.CountAsync() == 0, "Entry removal cascades episodes");
    }
    await using (var restarted = new ModDbContext(dbPath))
    {
        await restarted.Database.MigrateAsync();
        var appliedMigrations = (await restarted.Database.GetAppliedMigrationsAsync()).ToArray();
        // Every migration the assembly knows is applied; later phases add migrations, so the count is not fixed.
        Assert(await restarted.Entries.CountAsync() == 2 && appliedMigrations.Length >= 15 &&
            appliedMigrations.SequenceEqual(restarted.Database.GetMigrations()), "Restart preserves rows and migrations");
        Assert(appliedMigrations.Contains("20260910233343_PhaseTwoBindings"),
            "Published Phase 2 migration identity remains compatible with deployed databases");
        Assert(appliedMigrations.Contains("20260914125924_PhaseTwoBindingProvenance"),
            "Forward migration repairs binding provenance for already deployed databases");
        Assert(appliedMigrations.Contains("20260914224459_PhaseTwoAbsenceSummary"),
            "Phase 2 summaries receive missing-media and incomplete-library counters");
        Assert(appliedMigrations.Contains("20260915140337_PhaseThreeRetentionEvidence"),
            "Phase 3 adds durable retention policy and completion evidence");
        Assert(appliedMigrations.Contains("20260915223756_PhaseThreeRetentionEvaluation"),
            "Phase 3 adds durable access-aware retention evaluations");
        Assert(appliedMigrations.Contains("20260916120000_PhaseThreeRetentionOperations"),
            "Phase 3 adds durable recoverable reclamation operations");
        Assert(appliedMigrations.Contains("20260916130000_PhaseThreeRetentionRuns"),
            "Phase 3 adds durable scheduled and manual retention run summaries");
        Assert(appliedMigrations.Contains("20260919071544_PhaseFourAcquisition"),
            "Phase 4 adds acquisition settings, quality profiles and grab operations");
    }

    await VerifyHistoricalBindingSchemaAsync(folder);
    // Codex re-review P1-c: build 7009757 recorded the same bindings migration as 20260911042407_PhaseTwoBindings, before
    // PhaseTwoBackfill existed.
    await VerifyHistoricalBindingSchemaAsync(folder, "20260911042407_PhaseTwoBindings");
    await VerifyHistoricalRetentionOperationsAsync(folder);
    await ApiSmoke.RunAsync(folder);
    Console.WriteLine("PASS: real SQLite migration/restart and authenticated HTTP integration workflows");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(folder, true);
}

// Whole-review P1 10: build c2f8c25 created the binding tables with EntryBindings.TargetLibraryId and EpisodeBindings
// SeriesItemId and TargetLibraryId already present (its PhaseTwoBindings, under today's migration id), holding observed
// provenance. The schema is that build's: today's migrations up to PhaseTwoBackfill, plus exactly those columns as that
// build's CreateTable wrote them (TEXT NOT NULL). The real initializer must bring it to the current schema, ready, with
// the observed values kept.
static async Task VerifyHistoricalBindingSchemaAsync(string folder, string? recordedBindingsId = null)
{
    var dbPath = Path.Combine(folder, recordedBindingsId is null ? "historical-c2f8c25.db" : "historical-7009757.db");
    Guid libraryId = Guid.NewGuid(), observedLibrary = Guid.NewGuid(), seriesNative = Guid.NewGuid(), observedSeries = Guid.NewGuid();
    Guid movieId = Guid.NewGuid(), showId = Guid.NewGuid(), episodeId = Guid.NewGuid(), movieBinding = Guid.NewGuid(), episodeBinding = Guid.NewGuid();
    await using (var database = new ModDbContext(dbPath))
    {
        await database.GetService<IMigrator>().MigrateAsync(recordedBindingsId is null ? "20260911091353_PhaseTwoBackfill"
            : "20260910233343_PhaseTwoBindings");
        if (recordedBindingsId is not null)
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"__EFMigrationsHistory\" SET \"MigrationId\" = {recordedBindingsId} WHERE \"MigrationId\" = '20260910233343_PhaseTwoBindings'");
        foreach (var sql in new[]
                 {
                     "ALTER TABLE EntryBindings ADD COLUMN TargetLibraryId TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';",
                     "ALTER TABLE EpisodeBindings ADD COLUMN SeriesItemId TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';",
                     "ALTER TABLE EpisodeBindings ADD COLUMN TargetLibraryId TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';"
                 })
            await database.Database.ExecuteSqlRawAsync(sql);
        var now = DateTime.UtcNow;
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Entries (Id, MediaType, TmdbId, Title, State, Monitored, AddedAt, TargetLibraryId) VALUES ({movieId}, 'movie', 501, 'Historical movie', 4, 1, {now}, {libraryId})");
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Entries (Id, MediaType, TmdbId, Title, State, Monitored, AddedAt, TargetLibraryId, JellyfinItemId) VALUES ({showId}, 'series', 502, 'Historical show', 4, 1, {now}, {libraryId}, {seriesNative})");
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Episodes (Id, EntryId, TmdbId, SeasonNumber, EpisodeNumber, Title, Monitored, State) VALUES ({episodeId}, {showId}, 503, 1, 1, 'Pilot', 1, 4)");
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO EntryBindings (Id, EntryId, JellyfinItemId, VersionGroupId, TargetLibraryId) VALUES ({movieBinding}, {movieId}, {Guid.NewGuid()}, {Guid.NewGuid()}, {observedLibrary})");
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO EpisodeBindings (Id, EpisodeId, JellyfinItemId, SeriesItemId, TargetLibraryId) VALUES ({episodeBinding}, {episodeId}, {Guid.NewGuid()}, {observedSeries}, {observedLibrary})");
    }

    var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
    Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddTransient(services, _ => new ModDbContext(dbPath));
    await using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
    var initializer = new DatabaseInitializer(
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(provider),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseInitializer>.Instance);
    await initializer.StartAsync(default);
    // A second start over the upgraded database changes nothing and is ready again.
    var restarted = new DatabaseInitializer(
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(provider),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseInitializer>.Instance);
    await restarted.StartAsync(default);
    await using var upgraded = new ModDbContext(dbPath);
    var applied = (await upgraded.Database.GetAppliedMigrationsAsync()).ToArray();
    Assert(initializer.IsReady && restarted.IsReady && applied.SequenceEqual(upgraded.Database.GetMigrations()),
        $"A database written by build {(recordedBindingsId is null ? "c2f8c25" : "7009757")} upgrades to every migration, the plugin is ready, " +
        $"and a second start is ready too (whole-review P1 10, Codex re-review P1-c): ready={initializer.IsReady}/{restarted.IsReady}, " +
        $"{applied.Length} of {upgraded.Database.GetMigrations().Count()} applied");
    var movie = await upgraded.EntryBindings.AsNoTracking().SingleAsync(binding => binding.Id == movieBinding);
    var episode = await upgraded.EpisodeBindings.AsNoTracking().SingleAsync(binding => binding.Id == episodeBinding);
    Assert(movie.TargetLibraryId == observedLibrary && episode.SeriesItemId == observedSeries && episode.TargetLibraryId == observedLibrary,
        "The provenance that build observed is kept, not replaced by the derived values");
}

// Whole-review chunk 3c, P2 2: PhaseThreeRetentionOperations first shipped (3c0cd11) without ActionId, TargetLibraryId and
// StorageIdentity; they were added later by editing that migration. A database that applied its first form holds open and
// finished operations without them. Today's form of the migration, with those three columns and their index dropped
// again, is that schema. The real initializer must repair it so every later migration, the audit rebuild included,
// leaves readable rows: valid action identities, the entry's library, and no open operation without provenance.
static async Task VerifyHistoricalRetentionOperationsAsync(string folder)
{
    var dbPath = Path.Combine(folder, "historical-3c0cd11.db");
    Guid libraryId = Guid.NewGuid(), entryId = Guid.NewGuid(), openId = Guid.NewGuid(), doneId = Guid.NewGuid();
    await using (var database = new ModDbContext(dbPath))
    {
        await database.GetService<IMigrator>().MigrateAsync("20260916120000_PhaseThreeRetentionOperations");
        foreach (var sql in new[]
                 {
                     "DROP INDEX IF EXISTS IX_RetentionOperations_ActionId;",
                     "ALTER TABLE RetentionOperations DROP COLUMN ActionId;",
                     "ALTER TABLE RetentionOperations DROP COLUMN TargetLibraryId;",
                     "ALTER TABLE RetentionOperations DROP COLUMN StorageIdentity;"
                 })
            await database.Database.ExecuteSqlRawAsync(sql);
        var now = DateTime.UtcNow;
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Entries (Id, MediaType, TmdbId, Title, State, Monitored, AddedAt, TargetLibraryId) VALUES ({entryId}, 'movie', 601, 'Historical reclaim', 4, 1, {now}, {libraryId})");
        foreach (var (id, state) in new[] { (openId, "unlinked"), (doneId, "completed") })
            await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO RetentionOperations (Id, BindingId, EntryId, JellyfinItemId, PolicyVersion, MediaPath, PhysicalIdentity, LogicalBytes, HardlinkCountBefore, State, PreparedAt) VALUES ({id}, {Guid.NewGuid()}, {entryId}, {Guid.NewGuid()}, 1, '/media/historical.mkv', 'identity', 10, 1, {state}, {now})");
    }

    var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
    Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddTransient(services, _ => new ModDbContext(dbPath));
    await using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
    var initializer = new DatabaseInitializer(
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(provider),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseInitializer>.Instance);
    await initializer.StartAsync(default);
    await using var upgraded = new ModDbContext(dbPath);
    RetentionOperation[] operations;
    try
    {
        operations = await upgraded.RetentionOperations.AsNoTracking().OrderBy(operation => operation.State).ToArrayAsync();
    }
    catch (Exception error) when (error is FormatException or InvalidOperationException)
    {
        throw new InvalidOperationException("Historical retention operations are unreadable after the upgrade (whole-review c3cf2): " + error.Message);
    }

    var open = operations.Single(operation => operation.Id == openId);
    var done = operations.Single(operation => operation.Id == doneId);
    Assert(initializer.IsReady && (await upgraded.Database.GetAppliedMigrationsAsync()).SequenceEqual(upgraded.Database.GetMigrations()) &&
        open.ActionId == openId && done.ActionId == doneId && open.TargetLibraryId == libraryId && done.TargetLibraryId == libraryId,
        "An operation written before ActionId, TargetLibraryId and StorageIdentity existed upgrades with a valid action and its " +
        $"entry's library (whole-review c3cf2): {open.ActionId} {open.TargetLibraryId}");
    Assert(open.State == "failed" && open.Reason == "legacy_provenance_unknown" && done.State == "completed",
        $"An open operation without recorded storage provenance is failed, never recovered against a file: {open.State}/{open.Reason}");
}

static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task<T> Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T error) { return error; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}

internal sealed class BoundaryHttpFactory : IHttpClientFactory
{
    public string Body { get; set; } = "{}";
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public Uri BaseAddress { get; set; } = null!;
    public Func<Uri, HttpResponseMessage>? Response { get; set; }
    public Func<Uri, Task>? BeforeResponse { get; set; }
    public string? LastAuthorization { get; set; }
    public string? LastApiKey { get; set; }
    public HttpClient CreateClient(string name) => new(new BoundaryRedirect(BaseAddress));
    private sealed class BoundaryRedirect(Uri address) : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri = new Uri(address, request.RequestUri!.PathAndQuery);
            return base.SendAsync(request, cancellationToken);
        }
    }

}

/// <summary>Minimal host interface test double.</summary>
public class Stub<T> : DispatchProxy where T : class
{
    private Func<MethodInfo, object?[]?, object?> _callback = null!;
    /// <summary>Creates a test double.</summary>
    public static T Create(Func<MethodInfo, object?[]?, object?> callback)
    {
        var instance = DispatchProxy.Create<T, Stub<T>>();
        ((Stub<T>)(object)instance)._callback = callback;
        return instance;
    }
    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _callback(targetMethod!, args);
}
