using JellyfinMod.Data;
using JellyfinMod.Services;
using JellyfinMod.Services.Placeholders;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;

/// <summary>D279 through the real Phase 6 Replace and HTTP Remove paths, with distinct records for every native user.</summary>
internal sealed class UserDataKeepScenario(World world, PluginHost host, BaseItem old, BaseItem remaining)
{
    private readonly HashSet<Guid> _copiedBeforeUnlink = [];
    private static readonly DateTime PlayedAt = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    public static UserDataKeepScenario Seed(World world, PluginHost host, string oldPath, string remainingPath)
    {
        var old = world.Native.Items.Single(item => item.Path == oldPath);
        var remaining = world.Native.Items.Single(item => item.Path == remainingPath);
        var scenario = new UserDataKeepScenario(world, host, old, remaining);
        // Other versions involved in the Replace start empty, so they cannot supply the fixture's usual synthetic completion.
        foreach (var item in world.Native.Items.Where(item => Path.GetDirectoryName(item.Path) == Path.GetDirectoryName(oldPath)))
        {
            item.RunTimeTicks = TimeSpan.FromHours(1).Ticks;
            foreach (var user in world.Users)
                world.Native.UserData[(user.Id, item.Id)] = new UserItemData { Key = item.Id.ToString("N") };
        }

        foreach (var user in world.Users)
        {
            world.Native.UserData[(user.Id, old.Id)] = new UserItemData
            {
                Key = old.Id.ToString("N"), Played = true, IsFavorite = true, PlayCount = 3,
                LastPlayedDate = user.Id == world.Ordinary.Id ? PlayedAt.AddDays(-1) : PlayedAt,
                Rating = user.Id == world.Ordinary.Id ? 1 : 8,
                PlaybackPositionTicks = TimeSpan.FromMinutes(user.Id == world.MoviesOnly.Id ? 90 : 10).Ticks
            };
        }

        // A survivor's newer playback, larger count and existing rating must win while played/favourite merge in.
        world.Native.UserData[(world.RestrictedAdmin.Id, remaining.Id)] = new UserItemData
        {
            Key = remaining.Id.ToString("N"), PlayCount = 7, LastPlayedDate = PlayedAt.AddDays(1),
            Rating = 2, PlaybackPositionTicks = TimeSpan.FromMinutes(20).Ticks
        };
        world.Native.UserDataSaving = (userId, itemId) =>
        {
            Check(PlaceholderUserData.IsResaving, "The merge uses the quiet save, without retention completion events");
            if (itemId == remaining.Id && File.Exists(oldPath)) scenario._copiedBeforeUnlink.Add(userId);
        };
        return scenario;
    }

    public void Verify()
    {
        var manager = host.Service<IUserDataManager>();
        foreach (var user in new[] { world.Admin, world.Ordinary })
        {
            var data = manager.GetUserData(user, remaining)!;
            Check(data.Played && data.IsFavorite && data.PlayCount == 3 &&
                data.LastPlayedDate == (user.Id == world.Ordinary.Id ? PlayedAt.AddDays(-1) : PlayedAt) &&
                data.Rating == (user.Id == world.Ordinary.Id ? 1 : 8) && data.Likes == (user.Id != world.Ordinary.Id) &&
                data.PlaybackPositionTicks == TimeSpan.FromMinutes(10).Ticks,
                $"D279 keeps played, favourite, 3 plays, last played, rating/likes and valid resume for user {user.Id}");
        }

        var newer = manager.GetUserData(world.RestrictedAdmin, remaining)!;
        Check(newer.Played && newer.IsFavorite && newer.PlayCount == 7 && newer.LastPlayedDate == PlayedAt.AddDays(1) &&
            newer.Rating == 2 && newer.PlaybackPositionTicks == TimeSpan.FromMinutes(20).Ticks,
            "Newer survivor data wins while played/favourite merge");
        var tooLong = manager.GetUserData(world.MoviesOnly, remaining)!;
        Check(tooLong.Played && tooLong.PlayCount == 3 && tooLong.PlaybackPositionTicks == 0,
            "A different release does not inherit a resume beyond its runtime");
        Check(world.Users.All(user => _copiedBeforeUnlink.Contains(user.Id)), "Every user's copy precedes the file unlink");
        Check(!File.Exists(old.Path) && File.Exists(remaining.Path), "Only the requested old file was removed");
    }

    public static async Task RemoveDefaultAsync(World world, PluginHost host, HttpClient admin, string dbPath, Guid entryId,
        CapturingLoggerProvider logs)
    {
        EntryBinding source;
        EntryBinding survivor;
        await using (var database = new ModDbContext(dbPath))
        {
            var entry = await database.Entries.AsNoTracking().SingleAsync(value => value.Id == entryId);
            var bindings = await database.EntryBindings.AsNoTracking().Where(value => value.EntryId == entryId).ToListAsync();
            source = bindings.Single(binding => binding.JellyfinItemId == entry.JellyfinItemId);
            survivor = bindings.First(binding => binding.JellyfinItemId != source.JellyfinItemId);
        }

        var scenario = Seed(world, host, source.MediaPath!, survivor.MediaPath!);
        var newerWritten = false;
        var survivorSnapshotRead = false;
        var snapshots = new System.Collections.Concurrent.ConcurrentDictionary<BaseItem, Dictionary<Guid, UserItemData>>(
            ReferenceEqualityComparer.Instance);
        var currentSurvivor = world.Native.Items.Single(item => item.Id == survivor.JellyfinItemId);
        // Model Jellyfin's cached instance and separate database instances with their own loaded user data.
        world.Native.UserData[(world.RestrictedAdmin.Id, currentSurvivor.Id)] = new UserItemData { Key = currentSurvivor.Id.ToString("N") };
        Dictionary<Guid, UserItemData> Snapshot() => world.Users.ToDictionary(user => user.Id,
            user => world.Native.UserData[(user.Id, currentSurvivor.Id)]);
        snapshots[currentSurvivor] = Snapshot();
        world.Native.ItemRetrieved = item =>
        {
            if (item.Id != currentSurvivor.Id) return item;
            var fresh = new Movie { Id = item.Id, Path = item.Path, RunTimeTicks = item.RunTimeTicks, ProviderIds = item.ProviderIds };
            snapshots[fresh] = Snapshot();
            survivorSnapshotRead = true;
            return fresh;
        };
        world.Native.UserDataReading = (userId, item) =>
        {
            if (survivorSnapshotRead && !newerWritten && item.Id == source.JellyfinItemId && userId == world.Admin.Id)
            {
                newerWritten = true;
                // User B changes the stored survivor after CopyTo starts, before user A's merge.
                PlaceholderUserData.SaveQuietly(host.Service<IUserDataManager>(), world.RestrictedAdmin,
                    new Movie { Id = currentSurvivor.Id, Path = currentSurvivor.Path }, new UserItemData
                    {
                        Key = currentSurvivor.Id.ToString("N"), PlayCount = 7, LastPlayedDate = PlayedAt.AddDays(1),
                        Rating = 2, PlaybackPositionTicks = TimeSpan.FromMinutes(20).Ticks
                    }, CancellationToken.None);
            }

            return snapshots.TryGetValue(item, out var snapshot) ? snapshot[userId] : null;
        };
        try
        {
            using var response = await admin.PostAsync($"/JellyfinMod/Entries/{entryId}/Versions/{source.Id}/Remove", null);
            Check(response.IsSuccessStatusCode, "Remove This Version accepts the default copy: " + await response.Content.ReadAsStringAsync());
            world.Native.ItemRetrieved = null;
            world.Native.UserDataReading = null;
            scenario.Verify();
            Check(newerWritten && snapshots.Count >= world.Users.Count() + 1,
                "Each user merges a fresh survivor snapshot and user B keeps the change made before user A's merge");
        }
        finally
        {
            world.Native.UserDataSaving = null;
            world.Native.ItemRetrieved = null;
            world.Native.UserDataReading = null;
        }

        // The third copy remains: failing one user's copy must warn, still copy the other user, and keep the version until a
        // later attempt copies everyone (Codex review of ds.t7, P1).
        EntryBinding last;
        await using (var database = new ModDbContext(dbPath))
            last = await database.EntryBindings.AsNoTracking().SingleAsync(value => value.EntryId == entryId && value.Id != survivor.Id);
        var old = world.Native.Items.Single(item => item.Id == survivor.JellyfinItemId);
        world.Native.UserData[(world.Ordinary.Id, old.Id)] = new UserItemData
        {
            Key = old.Id.ToString("N"), Played = true, IsFavorite = true, PlayCount = 9, LastPlayedDate = PlayedAt.AddDays(2),
            PlaybackPositionTicks = TimeSpan.FromMinutes(5).Ticks
        };
        world.Native.Items.Single(item => item.Id == last.JellyfinItemId).RunTimeTicks = null;
        world.Native.UserData[(world.Admin.Id, last.JellyfinItemId)] = new UserItemData { Key = last.JellyfinItemId.ToString("N") };
        world.Native.UserData[(world.Ordinary.Id, last.JellyfinItemId)] = new UserItemData { Key = last.JellyfinItemId.ToString("N") };
        world.Native.UserDataSaving = (userId, itemId) =>
        {
            if (userId == world.Admin.Id && itemId == last.JellyfinItemId)
                throw new IOException("D279 injected user data write failure");
        };
        try
        {
            using var refused = await admin.PostAsync($"/JellyfinMod/Entries/{entryId}/Versions/{survivor.Id}/Remove", null);
            var refusal = await refused.Content.ReadAsStringAsync();
            Check((int)refused.StatusCode == 409 && refusal.Contains("user_data_not_kept", StringComparison.Ordinal) &&
                File.Exists(survivor.MediaPath) && File.Exists(last.MediaPath) &&
                world.Native.Items.Any(item => item.Id == survivor.JellyfinItemId),
                "A failed user copy refuses the removal and keeps the version and its item: " + refusal);
            await using (var database = new ModDbContext(dbPath))
                Check(!await database.RetentionOperations.AnyAsync(value => value.BindingId == survivor.Id),
                    "A refused removal records no operation");
            var kept = host.Service<IUserDataManager>().GetUserData(world.Ordinary,
                world.Native.Items.Single(item => item.Id == last.JellyfinItemId))!;
            Check(kept.Played && kept.IsFavorite && kept.PlayCount == 9 && kept.LastPlayedDate == PlayedAt.AddDays(2) &&
                kept.PlaybackPositionTicks == 0,
                "Another user's data is kept, without copying resume to a different release with unknown runtime");
            Check(logs.Lines.Any(line => line.StartsWith("Warning", StringComparison.Ordinal) &&
                line.Contains("D279 injected user data write failure", StringComparison.Ordinal)), "A failed copy is logged at warning level");
        }
        finally
        {
            world.Native.UserDataSaving = null;
        }

        // Once the write works again the same removal copies the failed user too and goes ahead.
        using (var response = await admin.PostAsync($"/JellyfinMod/Entries/{entryId}/Versions/{survivor.Id}/Remove", null))
        {
            Check(response.IsSuccessStatusCode && !File.Exists(survivor.MediaPath) && File.Exists(last.MediaPath),
                "The retried removal removes the version and keeps the survivor: " + await response.Content.ReadAsStringAsync());
            var retried = host.Service<IUserDataManager>().GetUserData(world.Admin,
                world.Native.Items.Single(item => item.Id == last.JellyfinItemId))!;
            Check(retried.Played && retried.IsFavorite, "The retry puts the failed user's data on the survivor");
        }

        // A second name for the very same content keeps resume even when the new item's runtime is not known yet.
        var hardlinkPath = Path.Combine(Path.GetDirectoryName(last.MediaPath)!, "D279 same content.mkv");
        Check(new UnixFileInspector().Link(last.MediaPath!, hardlinkPath).Linked, "The same-content fixture is a real hardlink");
        world.Native.AddMovie(world.Movies, hardlinkPath);
        var hardlink = world.Native.Items.Single(item => item.Path == hardlinkPath);
        Check(hardlink.ProviderIds.Count == 0, "The untagged local alternate has no provider metadata of its own");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            await using var database = new ModDbContext(dbPath);
            if (await database.EntryBindings.AnyAsync(value => value.EntryId == entryId && value.JellyfinItemId == hardlink.Id &&
                    value.FileFingerprint != null)) break;
            Check(DateTime.UtcNow < deadline, "The same-content survivor is reconciled");
            await Task.Delay(50);
        }

        Seed(world, host, last.MediaPath!, hardlinkPath);
        hardlink.RunTimeTicks = null;
        try
        {
            using var response = await admin.PostAsync($"/JellyfinMod/Entries/{entryId}/Versions/{last.Id}/Remove", null);
            var kept = host.Service<IUserDataManager>().GetUserData(world.MoviesOnly, hardlink)!;
            Check(response.IsSuccessStatusCode && !File.Exists(last.MediaPath) && File.Exists(hardlinkPath) &&
                kept.Played && kept.IsFavorite && kept.PlayCount == 3 && kept.LastPlayedDate == PlayedAt &&
                kept.PlaybackPositionTicks == TimeSpan.FromMinutes(90).Ticks,
                "The same content keeps resume without a known runtime");
        }
        finally
        {
            world.Native.UserDataSaving = null;
        }
    }

    public static async Task RemoveEpisodeAsync(World world, PluginHost host, HttpClient admin, string dbPath, Guid episodeId)
    {
        EpisodeBinding source;
        EpisodeBinding survivor;
        Guid entryId;
        await using (var database = new ModDbContext(dbPath))
        {
            var episode = await database.Episodes.AsNoTracking().SingleAsync(value => value.Id == episodeId);
            entryId = episode.EntryId;
            var bindings = await database.EpisodeBindings.AsNoTracking().Where(value => value.EpisodeId == episodeId).ToListAsync();
            source = bindings.Single(binding => binding.JellyfinItemId == episode.JellyfinItemId);
            survivor = bindings.First(binding => binding.JellyfinItemId != source.JellyfinItemId);
            // The fixture's s1e2 copies are bound by number only; this scenario models a verified episode, both copies.
            await database.EpisodeBindings.Where(value => value.Id == survivor.Id || value.Id == source.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.IdentityUnverified, false));
            survivor.IdentityUnverified = false;
        }

        var scenario = Seed(world, host, source.MediaPath!, survivor.MediaPath!);
        var nativeSource = (MediaBrowser.Controller.Entities.TV.Episode)world.Native.Items.Single(item => item.Id == source.JellyfinItemId);
        Check(!survivor.IdentityUnverified, "The episode survivor has a verified binding");
        var unverifiedPath = Path.Combine(Path.GetDirectoryName(source.MediaPath)!, "D279 unverified.mkv");
        await File.WriteAllBytesAsync(unverifiedPath, new byte[4096]);
        var unverified = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Id = Guid.NewGuid(), Path = unverifiedPath, SeriesId = nativeSource.SeriesId,
            ParentIndexNumber = nativeSource.ParentIndexNumber, IndexNumber = nativeSource.IndexNumber
        };
        world.Native.StoreUserDataKeepSibling(unverified);
        foreach (var user in world.Users)
            world.Native.UserData[(user.Id, unverified.Id)] = new UserItemData { Key = unverified.Id.ToString("N") };
        await using (var database = new ModDbContext(dbPath))
        {
            database.EpisodeBindings.Add(new EpisodeBinding
            {
                EpisodeId = episodeId, JellyfinItemId = unverified.Id, SeriesItemId = nativeSource.SeriesId,
                TargetLibraryId = source.TargetLibraryId, MediaPath = unverifiedPath, IdentityUnverified = true
            });
            await database.SaveChangesAsync();
        }

        try
        {
            // Stored metadata can be renumbered after binding without changing the verified episode, path or series.
            world.Native.ItemRetrieved = item => item.Id == survivor.JellyfinItemId
                ? new MediaBrowser.Controller.Entities.TV.Episode
                {
                    Id = item.Id, Path = item.Path, RunTimeTicks = item.RunTimeTicks, SeriesId = survivor.SeriesItemId,
                    ParentIndexNumber = nativeSource.ParentIndexNumber + 100, IndexNumber = nativeSource.IndexNumber + 100
                }
                : item;
            using var response = await admin.PostAsync($"/JellyfinMod/Entries/{entryId}/Versions/{source.Id}/Remove", null);
            Check(response.IsSuccessStatusCode, "An episode's default copy can be removed: " + await response.Content.ReadAsStringAsync());
            scenario.Verify();
            Check(world.Users.All(user => !world.Native.UserData[(user.Id, unverified.Id)].Played) &&
                !world.Native.SavedUserData.Any(saved => saved.ItemId == unverified.Id),
                "An IdentityUnverified sibling receives no user's data");
        }
        finally
        {
            world.Native.UserDataSaving = null;
            world.Native.ItemRetrieved = null;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed partial class NativeWorld
{
    // Seed the recorded sibling without raising a reconciliation event that could rewrite its test identity flag.
    public void StoreUserDataKeepSibling(BaseItem item) => _items[item.Id] = item;
}
