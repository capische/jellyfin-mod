using System.Xml.Serialization;
using JellyfinMod;
using JellyfinMod.Api;
using JellyfinMod.Data;
using JellyfinMod.Services.Acquisition;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var directory = Path.Combine(Path.GetTempPath(), $"jellyfinmod-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
try
{
    var path = Path.Combine(directory, "catalog;quoted.db");
    using var provider = new ServiceCollection()
        .AddLogging()
        .AddTransient(_ => new ModDbContext(path))
        .BuildServiceProvider();
    var initializer = NewInitializer(provider);
    Assert(HealthStatus(initializer, provider) == 503, "Health must be unavailable before migration");
    await initializer.StartAsync(CancellationToken.None);
    Assert(HealthStatus(initializer, provider) == 200, "Health must be ready after migration");

    var id = Guid.NewGuid();
    await using (var database = new ModDbContext(path))
    {
        Assert((await database.Database.GetAppliedMigrationsAsync()).Count() == database.Database.GetMigrations().Count(), "Initial migration applied once");
        database.Entries.Add(new Entry { Id = id, TmdbId = 123, Title = "Persistence probe" });
        database.History.Add(new HistoryRecord { EntryId = id, EventType = "added", Summary = "Persistence probe" });
        await database.SaveChangesAsync();
    }

    await initializer.StopAsync(CancellationToken.None);
    var restarted = NewInitializer(provider);
    await restarted.StartAsync(CancellationToken.None);
    await using (var database = new ModDbContext(path))
    {
        Assert(restarted.IsReady, "Restart initialization succeeds");
        Assert(await database.Entries.CountAsync() == 1 && await database.History.CountAsync() == 1,
            "Entries and history survive restart");
        Assert((await database.Database.GetAppliedMigrationsAsync()).Count() == database.Database.GetMigrations().Count(), "Restart does not reapply migration");
    }

    using var brokenProvider = new ServiceCollection()
        .AddLogging()
        .AddTransient(_ => new ModDbContext(directory)) // A directory cannot be opened as a database file.
        .BuildServiceProvider();
    var broken = NewInitializer(brokenProvider);
    await broken.StartAsync(CancellationToken.None);
    Assert(HealthStatus(broken, brokenProvider) == 503, "Failed migration reports unavailable without crashing the host");

    // Season and series packs (2026-10-08): a database written at DetailFileHistory upgrades its grabs to scopes and modes, and
    // every active single-episode grab claims its episode under the key it owns; releasing a grab releases its claim.
    var packsPath = Path.Combine(directory, "packs.db");
    var showId = Guid.NewGuid();
    var filmId = Guid.NewGuid();
    var firstEpisode = Guid.NewGuid();
    var secondEpisode = Guid.NewGuid();
    var activeGrab = Guid.NewGuid();
    var addGrab = Guid.NewGuid();
    var filmGrab = Guid.NewGuid();
    var doneGrab = Guid.NewGuid();
    await using (var database = new ModDbContext(packsPath))
    {
        await database.GetService<IMigrator>().MigrateAsync("20261007110102_DetailFileHistory");
        database.Entries.Add(new Entry { Id = showId, MediaType = "series", TmdbId = 1, Title = "Old Show" });
        database.Entries.Add(new Entry { Id = filmId, MediaType = "movie", TmdbId = 2, Title = "Old Movie" });
        database.Episodes.Add(new Episode { Id = firstEpisode, EntryId = showId, TmdbId = 11, SeasonNumber = 1, EpisodeNumber = 1, Title = "One" });
        database.Episodes.Add(new Episode { Id = secondEpisode, EntryId = showId, TmdbId = 12, SeasonNumber = 1, EpisodeNumber = 2, Title = "Two",
            State = FileState.OnDisk });
        await database.SaveChangesAsync();
        var now = DateTime.UtcNow;
        foreach (var (grabId, entryId, episodeId, target, intent, state) in new (Guid, Guid, Guid?, string?, string, string)[]
                 {
                     (activeGrab, showId, firstEpisode, firstEpisode.ToString("N"), "acquire", "accepted"),
                     (addGrab, showId, secondEpisode, secondEpisode.ToString("N") + "+add", "addVersion", "accepted"),
                     (filmGrab, filmId, null, filmId.ToString("N"), "acquire", "pending"),
                     (doneGrab, showId, secondEpisode, null, "acquire", "failed")
                 })
        {
            var key = "key-" + grabId.ToString("N");
            var other = Guid.NewGuid();
            const string parsed = "{}";
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO GrabOperations (Id, Automatic, CreatedAt, DownloadClientId, DownloadClientRevision, DownloadDirectory, EntryId,
                    EpisodeId, ActiveTarget, HoldUntil, IdempotencyKey, IndexerId, IndexerName, Intent, Label, ParsedJson, ProfileId,
                    ProfileRevision, RawTitle, ReleaseId, RequestFingerprint, RequestedBy, Score, ScoringVersion, SearchId, SourceGuid, State,
                    UpdatedAt)
                VALUES ({grabId}, 0, {now}, {other}, 1, '/downloads', {entryId}, {episodeId}, {target}, {now}, {key}, {other}, 'Indexer',
                    {intent}, 'jellyfinmod', {parsed}, {other}, 1, 'Old.Release', 'release', 'fingerprint', {other}, 0, 'p4.a4.v1', {other},
                    'guid', {state}, {now})
                """);
        }
    }

    await using (var database = new ModDbContext(packsPath))
    {
        await database.Database.MigrateAsync();
        var grabs = await database.GrabOperations.AsNoTracking().ToDictionaryAsync(grab => grab.Id);
        Assert(grabs[activeGrab].Scope == "episode" && grabs[activeGrab].Mode == "fill" && grabs[addGrab].Scope == "episode" &&
            grabs[addGrab].Mode == "add" && grabs[filmGrab].Scope == "title" && grabs[filmGrab].Mode == "fill" &&
            grabs[doneGrab].Scope == "episode" && grabs[doneGrab].SeasonNumber is null,
            "Existing grabs migrate to the title or episode scope, another version to add and the rest to fill");
        var claims = await database.GrabClaims.AsNoTracking().ToListAsync();
        Assert(claims.Count == 2 &&
            claims.Any(claim => claim.GrabId == activeGrab && claim.EpisodeId == firstEpisode && claim.ActiveKey == firstEpisode.ToString("N") && !claim.Held) &&
            claims.Any(claim => claim.GrabId == addGrab && claim.EpisodeId == secondEpisode && claim.ActiveKey == secondEpisode.ToString("N") + "+add" &&
                claim.Held),
            "Every active single-episode grab claims its episode under the key it owns; movies and finished grabs claim nothing");
        var taken = new GrabClaim { GrabId = doneGrab, EpisodeId = firstEpisode, ActiveKey = firstEpisode.ToString("N"), CreatedAt = DateTime.UtcNow };
        database.GrabClaims.Add(taken);
        var refused = false;
        try
        {
            await database.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            refused = true;
        }

        Assert(refused, "A second active claim on the same episode key is refused by the database");
        database.ChangeTracker.Clear();
        var released = await database.GrabOperations.SingleAsync(grab => grab.Id == activeGrab);
        released.ActiveTarget = null;
        await database.SaveChangesAsync();
        Assert((await database.GrabClaims.AsNoTracking().SingleAsync(claim => claim.GrabId == activeGrab)).ActiveKey is null &&
            (await database.GrabClaims.AsNoTracking().SingleAsync(claim => claim.GrabId == addGrab)).ActiveKey is not null,
            "Releasing a grab's target releases its claim (the GrabClaimRelease trigger) and no other");
    }

    // Pack re-review 2, P1 1 (2026-10-09): ImportOperations.ReplaceTargets was added inside the SeasonPacks migration, so a
    // database that applied SeasonPacks before then lacks it and every import read failed. Such a database is rebuilt here
    // exactly as that build left it (SeasonPacks applied, the column absent) with a replace import awaiting its removal; the
    // plugin's start adds the column empty, imports materialize, and the legacy import has no targets, so it removes nothing.
    var legacyPath = Path.Combine(directory, "season-packs-early.db");
    var legacyImport = Guid.NewGuid();
    await using (var database = new ModDbContext(legacyPath))
    {
        await database.Database.MigrateAsync();
        var legacyShow = Guid.NewGuid();
        var legacyEpisode = Guid.NewGuid();
        var legacyGrab = Guid.NewGuid();
        database.Entries.Add(new Entry { Id = legacyShow, MediaType = "series", TmdbId = 3, Title = "Early Pack Show" });
        database.Episodes.Add(new Episode { Id = legacyEpisode, EntryId = legacyShow, TmdbId = 31, SeasonNumber = 1, EpisodeNumber = 1,
            Title = "One", State = FileState.OnDisk });
        await database.SaveChangesAsync();
        var now = DateTime.UtcNow;
        var other = Guid.NewGuid();
        const string parsed = "{}";
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO GrabOperations (Id, Automatic, CreatedAt, DownloadClientId, DownloadClientRevision, DownloadDirectory, EntryId,
                EpisodeId, ActiveTarget, HoldUntil, IdempotencyKey, IndexerId, IndexerName, Intent, Label, ParsedJson, ProfileId,
                ProfileRevision, RawTitle, ReleaseId, RequestFingerprint, RequestedBy, Score, ScoringVersion, SearchId, SourceGuid, State,
                UpdatedAt, Scope, SeasonNumber, Mode)
            VALUES ({legacyGrab}, 0, {now}, {other}, 1, '/downloads', {legacyShow}, NULL, NULL, {now}, 'key-legacy', {other}, 'Indexer',
                'acquire', 'jellyfinmod', {parsed}, {other}, 1, 'Early.Pack.Show.S01', 'release', 'fingerprint', {other}, 0, 'p4.a4.v1', {other},
                'guid', 'completed', {now}, 'season', 1, 'replace')
            """);
        database.ImportOperations.Add(new ImportOperation
        {
            Id = legacyImport, GrabId = legacyGrab, EntryId = legacyShow, EpisodeId = legacyEpisode, InfoHash = new string('a', 40),
            ReleaseTitle = "Early.Pack.Show.S01", Intent = GrabIntents.AddVersion, State = ImportStates.Completed,
            ReplaceState = ReplaceStates.Pending, CreatedAt = now, UpdatedAt = now
        });
        await database.SaveChangesAsync();
        await database.Database.ExecuteSqlRawAsync("ALTER TABLE \"ImportOperations\" DROP COLUMN \"ReplaceTargets\";");
    }

    Assert(!await HasColumnAsync(legacyPath, "ImportOperations", "ReplaceTargets"),
        "The early SeasonPacks database is rebuilt without ReplaceTargets");
    using (var legacyProvider = new ServiceCollection().AddLogging().AddTransient(_ => new ModDbContext(legacyPath)).BuildServiceProvider())
    {
        for (var start = 1; start <= 2; start++)
        {
            var legacy = NewInitializer(legacyProvider);
            await legacy.StartAsync(CancellationToken.None);
            await using var database = new ModDbContext(legacyPath);
            var imports = await database.ImportOperations.AsNoTracking().ToListAsync();
            Assert(legacy.IsReady && await HasColumnAsync(legacyPath, "ImportOperations", "ReplaceTargets") &&
                (await database.Database.GetPendingMigrationsAsync()).Count() == 0 &&
                imports.Count == 1 && imports[0].Id == legacyImport && imports[0].ReplaceTargets is null &&
                imports[0].ReplaceState == ReplaceStates.Pending,
                $"Start {start} of an early SeasonPacks database adds ReplaceTargets once, imports materialize, and the legacy " +
                "replace import has no recorded targets");
        }
    }

    // The plain upgrade from the published 0.1.0.0 migrations creates the column through SeasonPacks itself.
    var publishedPath = Path.Combine(directory, "published-0100.db");
    await using (var database = new ModDbContext(publishedPath))
        await database.GetService<IMigrator>().MigrateAsync("20261002014147_WholeReviewQueueCleanupManifest");
    using (var publishedProvider = new ServiceCollection().AddLogging().AddTransient(_ => new ModDbContext(publishedPath)).BuildServiceProvider())
    {
        var published = NewInitializer(publishedProvider);
        await published.StartAsync(CancellationToken.None);
        await using var database = new ModDbContext(publishedPath);
        Assert(published.IsReady && (await database.Database.GetPendingMigrationsAsync()).Count() == 0 &&
            await HasColumnAsync(publishedPath, "ImportOperations", "ReplaceTargets") && await database.ImportOperations.CountAsync() == 0,
            "A 0.1.0.0 database upgrades through SeasonPacks to ReplaceTargets");
    }

    Assert(await HasColumnAsync(path, "ImportOperations", "ReplaceTargets"), "A fresh database has ReplaceTargets");

    var serializer = new XmlSerializer(typeof(PluginConfiguration));
    var selectedUserId = Guid.NewGuid();
    var config = new PluginConfiguration
    {
        TmdbReadAccessToken = "test-read-token",
        ReclaimAfterDays = 21,
        RetentionWatchedUserMode = WatchedUserMode.SelectedUser,
        RetentionSelectedUserId = selectedUserId,
        ExemptFavourites = false,
        TransmissionRpcUrl = "http://transmission.test/transmission/rpc",
        TransmissionUsername = "seed-reader",
        TransmissionPassword = "private-test-password"
    };
    using var serialized = new StringWriter();
    serializer.Serialize(serialized, config);
    using var reader = new StringReader(serialized.ToString());
    var restored = (PluginConfiguration)serializer.Deserialize(reader)!;
    Assert(restored.TmdbReadAccessToken == "test-read-token" && restored.ReclaimAfterDays == 21 &&
        restored.RetentionWatchedUserMode == WatchedUserMode.SelectedUser && restored.RetentionSelectedUserId == selectedUserId &&
        !restored.ExemptFavourites && !restored.RetentionEnabled &&
        restored.TransmissionRpcUrl == "http://transmission.test/transmission/rpc" &&
        restored.TransmissionUsername == "seed-reader" && restored.TransmissionPassword == "private-test-password",
        "Configuration survives XML round trip with retention disabled");
    Console.WriteLine("PASS: migrations, restart persistence, health readiness/failure, configuration XML");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(directory, recursive: true);
}

static DatabaseInitializer NewInitializer(ServiceProvider services) => new(
    services.GetRequiredService<IServiceScopeFactory>(),
    services.GetRequiredService<ILogger<DatabaseInitializer>>());

// The container is passed rather than the individual services: Health asks it for the interface subsystem and
// reports whatever it cannot find as absent, which is exactly the case this smoke host exercises.
static int? HealthStatus(DatabaseInitializer initializer, IServiceProvider services) =>
    ((ObjectResult)new HealthController(initializer, services).GetHealth().GetAwaiter().GetResult().Result!).StatusCode;

static async Task<bool> HasColumnAsync(string database, string table, string column)
{
    await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
    command.Parameters.AddWithValue("$table", table);
    command.Parameters.AddWithValue("$column", column);
    return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
