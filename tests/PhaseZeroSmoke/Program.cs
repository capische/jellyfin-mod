using System.Xml.Serialization;
using JellyfinMod;
using JellyfinMod.Api;
using JellyfinMod.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    Assert(HealthStatus(initializer) == 503, "Health must be unavailable before migration");
    await initializer.StartAsync(CancellationToken.None);
    Assert(HealthStatus(initializer) == 200, "Health must be ready after migration");

    var id = Guid.NewGuid();
    await using (var database = new ModDbContext(path))
    {
        Assert((await database.Database.GetAppliedMigrationsAsync()).Count() == 1, "Initial migration applied once");
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
        Assert((await database.Database.GetAppliedMigrationsAsync()).Count() == 1, "Restart does not reapply migration");
    }

    using var brokenProvider = new ServiceCollection()
        .AddLogging()
        .AddTransient(_ => new ModDbContext(directory)) // A directory cannot be opened as a database file.
        .BuildServiceProvider();
    var broken = NewInitializer(brokenProvider);
    await broken.StartAsync(CancellationToken.None);
    Assert(HealthStatus(broken) == 503, "Failed migration reports unavailable without crashing the host");

    var serializer = new XmlSerializer(typeof(PluginConfiguration));
    var config = new PluginConfiguration { ReclaimAfterDays = 21, ExemptFavourites = false };
    using var serialized = new StringWriter();
    serializer.Serialize(serialized, config);
    using var reader = new StringReader(serialized.ToString());
    var restored = (PluginConfiguration)serializer.Deserialize(reader)!;
    Assert(restored.ReclaimAfterDays == 21 && !restored.ExemptFavourites && !restored.RetentionEnabled,
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

static int? HealthStatus(DatabaseInitializer initializer) =>
    ((ObjectResult)new HealthController(initializer).GetHealth().Result!).StatusCode;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
