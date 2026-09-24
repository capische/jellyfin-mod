using System.Diagnostics;
using System.Text.Json;
using JellyfinMod.Data;
using JellyfinMod.Services.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Length == 4 && args[0] == "--crash-after-swap")
{
    var childStore = new WebBundleStore(args[1], Path.GetDirectoryName(args[2])!, NullLogger<WebBundleStore>.Instance);
    await childStore.InstallAsync(7, CancellationToken.None);
    var child = new WebRootTakeover(args[3], childStore, new ExitAfterSwapLogger());
    await child.ReconcileAsync(true, Path.Combine(args[3], "web"), "startup", default);
    throw new Exception("Expected process termination after the live swap");
}

var package = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppContext.BaseDirectory, "jellyfinmod-web.zip");
var root = Path.Combine(Path.GetTempPath(), "jfmod-takeover-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var store = new WebBundleStore(Path.Combine(root, "bundles"), Path.GetDirectoryName(package)!, NullLogger<WebBundleStore>.Instance);
    await store.InstallAsync(7, CancellationToken.None);
    Check(store.Current is not null, "packaged web bundle installed");

    // ---- At most three bundles on disk (§4.5): four earlier bundles within their grace period, then a restart.
    var bundlesRoot = Path.Combine(root, "bundles", "web");
    var earlier = new Dictionary<string, DateTime>();
    for (var index = 1; index <= 4; index++)
    {
        var id = $"00000000000{index}";
        Directory.CreateDirectory(Path.Combine(bundlesRoot, id));
        File.WriteAllText(Path.Combine(bundlesRoot, id, "jellyfinmod.html"), "earlier bundle");
        earlier[id] = DateTime.UtcNow.AddHours(-index);
    }
    earlier[store.Current!.BundleId] = DateTime.UtcNow.AddHours(-5);
    File.WriteAllText(Path.Combine(bundlesRoot, "retained.json"), JsonSerializer.Serialize(earlier));
    var restartedStore = new WebBundleStore(Path.Combine(root, "bundles"), Path.GetDirectoryName(package)!, NullLogger<WebBundleStore>.Instance);
    await restartedStore.InstallAsync(14, CancellationToken.None);
    Check(restartedStore.RetainedBundleIds.SequenceEqual([store.Current.BundleId, "000000000001", "000000000002"]),
        "three bundles retained: the current one and the two most recent (" + string.Join(",", restartedStore.RetainedBundleIds) + ")");
    Check(!Directory.Exists(Path.Combine(bundlesRoot, "000000000003")) && !Directory.Exists(Path.Combine(bundlesRoot, "000000000004")) &&
        Directory.Exists(Path.Combine(bundlesRoot, "000000000002")), "older bundles are pruned from disk at once, inside their grace period");

    (WebRootTakeover Engine, string Index, string Stock) Scenario(string name, string prefix = "")
    {
        var web = Path.Combine(root, name, "web");
        Directory.CreateDirectory(web);
        var index = Path.Combine(web, "index.html");
        var stock = "<!doctype html><html><head></head><body>stock login and Dashboard</body></html>";
        File.WriteAllText(index, stock);
        var engine = new WebRootTakeover(Path.Combine(root, name, "data"), store, NullLogger<WebRootTakeover>.Instance, prefix);
        return (engine, index, stock);
    }

    var normal = Scenario("normal");
    Check((await normal.Engine.ReconcileAsync(true, Path.GetDirectoryName(normal.Index), "startup", default)).Status == "patched", "normal patch");
    var rendered = File.ReadAllText(normal.Index);
    Check(rendered.Contains($"/web-mod/{store.Current!.BundleId}/", StringComparison.Ordinal), "empty base URL asset root");
    Check(rendered.Contains("function redirectToStock()", StringComparison.Ordinal), "fallback script installed");
    Check((await normal.Engine.ReconcileAsync(false, Path.GetDirectoryName(normal.Index), "setting", default)).Status == "stock", "normal restore");
    Check(File.ReadAllText(normal.Index) == normal.Stock, "normal original bytes restored");

    var prefixed = Scenario("prefixed", "/jellyfin/");
    Check((await prefixed.Engine.ReconcileAsync(true, Path.GetDirectoryName(prefixed.Index), "startup", default)).Status == "patched", "prefixed patch");
    Check(File.ReadAllText(prefixed.Index).Contains($"/jellyfin/web-mod/{store.Current.BundleId}/", StringComparison.Ordinal), "prefixed assets");
    var movedPrefix = new WebRootTakeover(Path.Combine(root, "prefixed", "data"), store, NullLogger<WebRootTakeover>.Instance, "/media");
    Check((await movedPrefix.ReconcileAsync(true, Path.GetDirectoryName(prefixed.Index), "startup", default)).Status == "patched", "changed prefix re-renders");
    Check(File.ReadAllText(prefixed.Index).Contains($"/media/web-mod/{store.Current.BundleId}/", StringComparison.Ordinal), "changed prefix assets");
    Check((await prefixed.Engine.ReconcileAsync(false, Path.GetDirectoryName(prefixed.Index), "setting", default)).Status == "stock", "prefixed restore");
    Check(File.ReadAllText(prefixed.Index) == prefixed.Stock, "prefixed original bytes restored");

    var failedRecord = Scenario("state-failure");
    var stateDir = Path.Combine(root, "state-failure", "data", "web-root");
    Directory.CreateDirectory(stateDir);
    var stateTemp = Path.Combine(stateDir, "state.json.jellyfinmod-tmp");
    Directory.CreateDirectory(stateTemp);
    Check((await failedRecord.Engine.ReconcileAsync(true, Path.GetDirectoryName(failedRecord.Index), "startup", default)).Status == "inconsistent", "state write fails");
    Check(File.ReadAllText(failedRecord.Index) == failedRecord.Stock, "state write failure leaves live stock intact");
    Directory.Delete(stateTemp);
    var restarted = new WebRootTakeover(Path.Combine(root, "state-failure", "data"), store, NullLogger<WebRootTakeover>.Instance);
    Check((await restarted.ReconcileAsync(true, Path.GetDirectoryName(failedRecord.Index), "startup", default)).Status == "patched", "retry after state failure");
    Check((await restarted.ReconcileAsync(false, Path.GetDirectoryName(failedRecord.Index), "setting", default)).Status == "stock", "restore after state failure");
    Check(File.ReadAllText(failedRecord.Index) == failedRecord.Stock, "state failure original bytes restored");

    var failedIndex = Scenario("index-failure");
    var indexTemp = failedIndex.Index + ".jellyfinmod-tmp";
    Directory.CreateDirectory(indexTemp);
    Check((await failedIndex.Engine.ReconcileAsync(true, Path.GetDirectoryName(failedIndex.Index), "startup", default)).Status == "inconsistent", "index write fails after state save");
    Check(File.Exists(Path.Combine(root, "index-failure", "data", "web-root", "state.json")), "recovery record saved first");
    Check(File.ReadAllText(failedIndex.Index) == failedIndex.Stock, "index failure leaves stock live");
    Directory.Delete(indexTemp);
    restarted = new WebRootTakeover(Path.Combine(root, "index-failure", "data"), store, NullLogger<WebRootTakeover>.Instance);
    Check((await restarted.ReconcileAsync(true, Path.GetDirectoryName(failedIndex.Index), "startup", default)).Status == "patched", "restart after index failure");
    Check((await restarted.ReconcileAsync(false, Path.GetDirectoryName(failedIndex.Index), "setting", default)).Status == "stock", "restore after index failure");
    Check(File.ReadAllText(failedIndex.Index) == failedIndex.Stock, "index failure original bytes restored");

    var interrupted = Scenario("interrupted");
    var executable = Environment.ProcessPath!;
    var start = new ProcessStartInfo(executable) { UseShellExecute = false };
    if (Path.GetFileNameWithoutExtension(executable) == "dotnet")
        start.ArgumentList.Add(typeof(WebRootTakeover).Assembly.Location.Replace("JellyfinMod.dll", "PhaseSevenTakeoverIntegration.dll", StringComparison.Ordinal));
    start.ArgumentList.Add("--crash-after-swap");
    start.ArgumentList.Add(Path.Combine(root, "bundles"));
    start.ArgumentList.Add(package);
    start.ArgumentList.Add(Path.Combine(root, "interrupted", "data"));
    // The child uses its own disposable web directory, just as a restarted host would.
    Directory.CreateDirectory(Path.Combine(root, "interrupted", "data", "web"));
    File.Copy(interrupted.Index, Path.Combine(root, "interrupted", "data", "web", "index.html"));
    using (var process = Process.Start(start)!)
    {
        await process.WaitForExitAsync();
        Check(process.ExitCode == 71, "process exits immediately after live swap");
    }
    var childIndex = Path.Combine(root, "interrupted", "data", "web", "index.html");
    Check(WebDocumentRenderer.IsRendered(File.ReadAllText(childIndex)), "child had swapped live document");
    var afterCrash = new WebRootTakeover(Path.Combine(root, "interrupted", "data"), store, NullLogger<WebRootTakeover>.Instance);
    Check((await afterCrash.ReconcileAsync(true, Path.GetDirectoryName(childIndex), "startup", default)).Status == "patched", "restart after process death");
    Check((await afterCrash.ReconcileAsync(false, Path.GetDirectoryName(childIndex), "setting", default)).Status == "stock", "restore after process death");
    Check(File.ReadAllText(childIndex) == interrupted.Stock, "interrupted original bytes restored");

    // ---- History rows (REVIEW-2026-09-24 S4-R4): every patch and restore leaves exactly one row in a real, migrated
    //      SQLite database, with ids, hashes and who applied it, and never a path.
    var historyDb = Path.Combine(root, "history.db");
    await using (var database = new ModDbContext(historyDb))
        await database.Database.MigrateAsync();
    await using var services = new ServiceCollection().AddTransient(_ => new ModDbContext(historyDb)).BuildServiceProvider();
    var history = new TakeoverHistory(services.GetRequiredService<IServiceScopeFactory>());
    var recorded = Scenario("history");
    var historyWeb = Path.GetDirectoryName(recorded.Index)!;
    var historyData = Path.Combine(root, "history", "data");
    WebRootTakeover Engine() => new(historyData, store, NullLogger<WebRootTakeover>.Instance, "", history.RecordAsync);
    async Task<List<HistoryRecord>> Rows()
    {
        await using var database = new ModDbContext(historyDb);
        return await database.History.AsNoTracking().OrderBy(row => row.CreatedAt).ToListAsync();
    }

    string Field(HistoryRecord row, string name) =>
        JsonDocument.Parse(row.Data!).RootElement.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
    var engine = Engine();
    Check((await engine.ReconcileAsync(true, historyWeb, "startup", default)).PatchedBy == "automatic", "fresh install reports automatic");
    var rows = await Rows();
    Check(rows.Count == 1 && rows[0].EventType == "interface_patched" && Field(rows[0], "patchedBy") == "automatic" &&
        Field(rows[0], "bundleId") == store.Current!.BundleId && Field(rows[0], "stockSha256").Length == 64 &&
        Field(rows[0], "patchedSha256").Length == 64 && rows[0].EntryId == Guid.Empty, "fresh install writes one interface_patched row, automatic");
    Check((await Engine().ReconcileAsync(true, historyWeb, "startup", default)).Status == "patched" && (await Rows()).Count == 1,
        "a restart with nothing to change writes no row");
    await engine.ReconcileAsync(false, historyWeb, "setting", default);
    rows = await Rows();
    Check(rows.Count == 2 && rows[1].EventType == "interface_restored" && Field(rows[1], "restoredBy") == "setting" &&
        Field(rows[1], "stockSha256") == Field(rows[0], "stockSha256"), "takeover off writes one interface_restored row");
    Check((await engine.ReconcileAsync(true, historyWeb, "setting", default)).PatchedBy == "setting", "takeover on reports setting");
    rows = await Rows();
    Check(rows.Count == 3 && rows[2].EventType == "interface_patched" && Field(rows[2], "patchedBy") == "setting", "takeover on writes one row, setting");
    // A bundle change: the record names the bundle the page was rendered for; a different one is installed now.
    var statePath = Path.Combine(historyData, "web-root", "state.json");
    File.WriteAllText(statePath, File.ReadAllText(statePath).Replace(store.Current.BundleId, "0123456789ab", StringComparison.Ordinal));
    Check((await Engine().ReconcileAsync(true, historyWeb, "startup", default)).PatchedBy == "bundle", "a bundle change reports bundle");
    rows = await Rows();
    Check(rows.Count == 4 && Field(rows[3], "patchedBy") == "bundle" && Field(rows[3], "previousBundleId") == "0123456789ab" &&
        Field(rows[3], "bundleId") == store.Current.BundleId, "a bundle change writes one row naming both bundles");
    await Engine().ReconcileAsync(false, historyWeb, "restoreStock", default);
    rows = await Rows();
    Check(rows.Count == 5 && rows[4].EventType == "interface_restored" && Field(rows[4], "restoredBy") == "restoreStock" &&
        File.ReadAllText(recorded.Index) == recorded.Stock, "Restore stock now writes one row and restores the original bytes");
    Check(rows.All(row => !row.Data!.Contains(root, StringComparison.Ordinal) && !row.Summary.Contains(root, StringComparison.Ordinal)),
        "no history row carries a path");
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    Console.WriteLine("PASS: real filesystem patch, URL prefix, both write failures, process death, restart, exact restore and history rows");
}

finally
{
    Directory.Delete(root, true);
}

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
}

sealed class ExitAfterSwapLogger : ILogger<WebRootTakeover>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Information && formatter(state, exception).Contains("replaced the Jellyfin web interface", StringComparison.Ordinal))
            Environment.Exit(71);
    }
}
