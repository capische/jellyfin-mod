using JellyfinMod.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TvEpisode = MediaBrowser.Controller.Entities.TV.Episode;

namespace JellyfinMod.Services.Trakt;

/// <summary>An episode or movie the Trakt plugin's export logged as not found on Trakt (its ids, or its season and number).</summary>
public sealed record TraktNotFound(bool Movie, string? Tvdb, string? Tmdb, string? Imdb, int? Season, int? Number);

/// <summary>
/// Settings › Trakt's per-title activity (Phase 13, D125–D127): one row per title, direction and user with its latest
/// state, in the plugin's database, the last 30 days. Writes never throw: a failed write is logged and the export goes on.
/// </summary>
public sealed class TraktActivityLog(IServiceScopeFactory scopes, ILibraryManager library, ILogger<TraktActivityLog> logger)
{
    /// <summary>More titles than this at once (a first export of older watches, a first import) read as one count row.</summary>
    public const int BurstLimit = 50;

    private static readonly TimeSpan Kept = TimeSpan.FromDays(30);
    private readonly SemaphoreSlim _write = new(1, 1);

    /// <summary>A watch here by a linked user: waiting for the export.</summary>
    public Task WatchedAsync(Guid userId, BaseItem item, DateTime at) => UpsertAsync(userId, item, "to", "waiting", null, at);

    /// <summary>A live play by a linked user with the Trakt plugin's Scrobble on: the plugin reports it to Trakt as it goes.</summary>
    public Task PlayingAsync(Guid userId, BaseItem item) => UpsertAsync(userId, item, "to", "playing", null, DateTime.UtcNow);

    /// <summary>A live play stopped before its end (the plugin reports where it stopped); only a row still playing changes.</summary>
    public Task StoppedAsync(Guid userId, BaseItem item, int percent) =>
        WriteAsync(async database =>
        {
            var row = await database.TraktActivity.FirstOrDefaultAsync(entry => entry.UserId == userId && entry.ItemId == item.Id && entry.Direction == "to").ConfigureAwait(false);
            if (row is null || row.State != "playing") return;
            row.State = "stopped";
            row.Detail = percent;
            row.At = DateTime.UtcNow;
        });

    /// <summary>
    /// A live play the Trakt plugin scrobbled to its end while its watched history is not posted: the play row ends as
    /// scrobbled, since no export will send it. Only a row still playing changes.
    /// </summary>
    public Task FinishedLiveAsync(Guid userId, BaseItem item) =>
        WriteAsync(async database =>
        {
            var row = await database.TraktActivity.FirstOrDefaultAsync(entry => entry.UserId == userId && entry.ItemId == item.Id && entry.Direction == "to").ConfigureAwait(false);
            if (row is null || row.State != "playing") return;
            row.State = "scrobbled";
            row.Detail = null;
            row.At = DateTime.UtcNow;
        });

    /// <summary>
    /// What one run of the Trakt plugin's import saved here: per title, or one count row past the burst limit. A save that
    /// leaves a resume point is a playback position (Trakt's paused list), even on a title already watched; one without is a
    /// watch (Codex review of the additions, P3 4).
    /// </summary>
    public Task ImportedAsync(IReadOnlyList<(Guid UserId, Guid ItemId, bool Played, long PositionTicks, DateTime At)> saves) =>
        WriteAsync(async database =>
        {
            foreach (var group in saves.GroupBy(save => save.UserId))
            {
                var distinct = group.GroupBy(save => save.ItemId).Select(item => item.Last()).ToList();
                if (distinct.Count > BurstLimit)
                {
                    database.TraktActivity.Add(new TraktActivityEntry
                    {
                        UserId = group.Key, Direction = "from", State = "summary", Detail = distinct.Count(save => save.Played),
                        Kind = "episode", Title = string.Empty, At = distinct.Max(save => save.At)
                    });
                    continue;
                }

                foreach (var save in distinct)
                {
                    if (library.GetItemById(save.ItemId) is not { } item) continue;
                    var position = save.PositionTicks > 0;
                    if (!position && !save.Played) continue;
                    await SetAsync(database, group.Key, item, "from", position ? "position" : "imported",
                        position ? (int)TimeSpan.FromTicks(save.PositionTicks).TotalMinutes : null, save.At).ConfigureAwait(false);
                }
            }
        });

    /// <summary>
    /// A judged export run: the titles it could not send (logged as not found) read failed; when it was clean, every title
    /// waiting since before it started reads sent. Collection adds, and a burst of more than 50 sent at once, are count rows.
    /// </summary>
    public Task ExportedAsync(IReadOnlyCollection<Guid> userIds, DateTime started, bool clean, IReadOnlyList<TraktNotFound> notFound,
        int? episodesSent, int? collected) =>
        WriteAsync(async database =>
        {
            var waiting = await database.TraktActivity
                .Where(entry => entry.Direction == "to" && entry.State == "waiting" && entry.At <= started && userIds.Contains(entry.UserId))
                .ToListAsync().ConfigureAwait(false);
            var items = waiting.Select(row => row.ItemId is { } id ? library.GetItemById(id) : null).ToList();
            for (var index = 0; index < waiting.Count; index++)
            {
                var row = waiting[index];
                if (notFound.Count > 0 && items[index] is { } item && NotFound(item, notFound, items))
                {
                    row.State = "failed";
                }
                else if (clean)
                {
                    row.State = "sent";
                }
            }

            // The count lines are what the plugin meant to send, logged before it sent: only a clean run delivered them. One row
            // per run and kind: a run judged again (after a restart) finds its row by its start (Codex review of the additions, P2 2).
            var user = userIds.FirstOrDefault();
            if (user == Guid.Empty || !clean) return;
            async Task CountAsync(string state, int count)
            {
                if (await database.TraktActivity.AnyAsync(entry => entry.UserId == user && entry.Direction == "to" && entry.State == state && entry.ItemId == null &&
                    entry.At >= started.AddSeconds(-1) && entry.At <= started.AddSeconds(1)).ConfigureAwait(false)) return;
                database.TraktActivity.Add(new TraktActivityEntry { UserId = user, Direction = "to", State = state, Detail = count, Title = string.Empty, At = started });
            }

            if (episodesSent > BurstLimit) await CountAsync("summary", episodesSent.Value).ConfigureAwait(false);
            if (collected > 0) await CountAsync("collection", collected.Value).ConfigureAwait(false);
        });

    /// <summary>The newest rows, at most <paramref name="count"/>.</summary>
    public async Task<IReadOnlyList<TraktActivityEntry>> ReadAsync(int count)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ModDbContext>();
            var cutoff = DateTime.UtcNow - Kept;
            return await database.TraktActivity.AsNoTracking().Where(entry => entry.At >= cutoff).OrderByDescending(entry => entry.At)
                .Take(count).ToListAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("JellyfinMod could not read the Trakt activity: {Error}", error.Message);
            return [];
        }
    }

    private static bool NotFound(BaseItem item, IReadOnlyList<TraktNotFound> notFound, IReadOnlyList<BaseItem?> waiting)
    {
        var movie = item is Movie;
        string? Id(MetadataProvider provider) => item.GetProviderId(provider);
        foreach (var entry in notFound.Where(entry => entry.Movie == movie))
        {
            if ((entry.Tvdb is not null && entry.Tvdb == Id(MetadataProvider.Tvdb)) || (entry.Tmdb is not null && entry.Tmdb == Id(MetadataProvider.Tmdb)) ||
                (entry.Imdb is not null && entry.Imdb == Id(MetadataProvider.Imdb))) return true;
            // Sent by series, season and number (an episode without ids of its own): Trakt names only the season and number, so
            // it matches when exactly one waiting episode has them.
            if (entry.Tvdb is null && entry.Tmdb is null && entry.Imdb is null && entry.Number > 0 && item is TvEpisode episode &&
                entry.Season == episode.ParentIndexNumber && entry.Number == episode.IndexNumber &&
                waiting.Count(other => other is TvEpisode same && same.ParentIndexNumber == entry.Season && same.IndexNumber == entry.Number) == 1) return true;
        }

        return false;
    }

    private Task UpsertAsync(Guid userId, BaseItem item, string direction, string state, int? detail, DateTime at) =>
        WriteAsync(database => SetAsync(database, userId, item, direction, state, detail, at));

    private static async Task SetAsync(ModDbContext database, Guid userId, BaseItem item, string direction, string state, int? detail, DateTime at)
    {
        var row = await database.TraktActivity.FirstOrDefaultAsync(entry => entry.UserId == userId && entry.ItemId == item.Id && entry.Direction == direction).ConfigureAwait(false);
        if (row is null)
        {
            row = new TraktActivityEntry { UserId = userId, ItemId = item.Id, Direction = direction };
            database.TraktActivity.Add(row);
        }

        (row.Title, row.Code, row.Kind) = Label(item);
        row.State = state;
        row.Detail = detail;
        row.At = at;
    }

    /// <summary>"The Office" and "S02E04" for an episode, the name for a movie.</summary>
    private static (string Title, string? Code, string Kind) Label(BaseItem item) => item is TvEpisode episode
        ? (Clip(episode.SeriesName ?? episode.Name ?? string.Empty), $"S{episode.ParentIndexNumber ?? 0:00}E{episode.IndexNumber ?? 0:00}", "episode")
        : (Clip(item.Name ?? string.Empty), null, "movie");

    private static string Clip(string value) => value.Length > 300 ? value[..300] : value;

    private async Task WriteAsync(Func<ModDbContext, Task> change)
    {
        await _write.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<ModDbContext>();
            await change(database).ConfigureAwait(false);
            var cutoff = DateTime.UtcNow - Kept;
            database.TraktActivity.RemoveRange(await database.TraktActivity.Where(entry => entry.At < cutoff).ToListAsync().ConfigureAwait(false));
            await database.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogWarning("JellyfinMod could not write the Trakt activity: {Error}", error.Message);
        }
        finally
        {
            _write.Release();
        }
    }
}
