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
        Assert(await restarted.Entries.CountAsync() == 2 && appliedMigrations.Length == 10, "Restart preserves rows and migrations");
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
    }

    await ApiSmoke.RunAsync(folder);
    Console.WriteLine("PASS: real SQLite migration/restart and authenticated HTTP integration workflows");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(folder, true);
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
