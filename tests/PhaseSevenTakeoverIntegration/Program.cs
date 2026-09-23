using System.Diagnostics;
using JellyfinMod.Services.Web;
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

    Console.WriteLine("PASS: real filesystem patch, URL prefix, both write failures, process death, restart and exact restore");
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
