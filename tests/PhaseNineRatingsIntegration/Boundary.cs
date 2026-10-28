using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// A real HTTP boundary for MDBList and TMDB (PHASE9 acceptance conventions): it serves MDBList's
/// <c>GET /tmdb/{movie|show}/{id}?apikey=</c> shape and TMDB's <c>/3/movie|tv/{id}</c>, scripted per mode. It counts calls and
/// whether the expected key arrived, and never stores or prints a key.
/// </summary>
internal sealed class Boundary : IAsyncDisposable
{
    private readonly WebApplication _app;

    private Boundary(WebApplication app, Uri address)
    {
        _app = app;
        Address = address;
    }

    public Uri Address { get; }

    /// <summary>The key the boundary accepts; anything else is answered as a refused key.</summary>
    public string ExpectedKey { get; set; } = string.Empty;

    /// <summary>The mode every MDBList call gets unless a title has its own.</summary>
    public string Mode { get; set; } = "full";

    public ConcurrentDictionary<int, string> TitleModes { get; } = new();

    /// <summary>MDBList calls in arrival order, as "movie:9101" — never the query string.</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    public int WrongKeyCalls;

    /// <summary>A second key accepted while a test replaces the key under a running fetch.</summary>
    public string? AlsoAccept { get; set; }

    /// <summary>Each MDBList call with a short fingerprint of the key it carried (never the key itself), in arrival order.</summary>
    public ConcurrentQueue<(string Call, string KeyPrint)> CallLog { get; } = new();

    /// <summary>The fingerprint <see cref="CallLog"/> records for a key.</summary>
    public static string Print(string key) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..8];

    /// <summary>The X-RateLimit-Reset the "bothheaders" mode advertises.</summary>
    public DateTimeOffset ResetAt { get; set; }

    /// <summary>When set, a call for this title waits until <see cref="Release"/> completes.</summary>
    public int? HoldTitle { get; set; }

    /// <summary>When set, the next call (whatever its title) waits until <see cref="Release"/> completes.</summary>
    public bool HoldAny { get; set; }

    public TaskCompletionSource Held { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ResetHold()
    {
        Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HoldTitle = null;
        HoldAny = false;
    }

    public int CallsFor(string kind, int id) => Calls.Count(call => call == $"{kind}:{id}");

    /// <summary>How long every MDBList call takes before it is answered (a pass in progress is visible this way).</summary>
    public TimeSpan Delay { get; set; }

    private int _inFlight;

    /// <summary>The most MDBList calls ever open at once: one fetcher means one.</summary>
    public int MaxInFlight;

    public static async Task<Boundary> StartAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddRouting();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        Boundary? self = null;
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/tmdb"))
            {
                await next();
                return;
            }

            var boundary = self!;
            var open = Interlocked.Increment(ref boundary._inFlight);
            for (var seen = Volatile.Read(ref boundary.MaxInFlight); open > seen; seen = Volatile.Read(ref boundary.MaxInFlight))
                if (Interlocked.CompareExchange(ref boundary.MaxInFlight, open, seen) == seen) break;
            try
            {
                if (boundary.Delay > TimeSpan.Zero) await Task.Delay(boundary.Delay);
                await next();
            }
            finally
            {
                Interlocked.Decrement(ref boundary._inFlight);
            }
        });
        app.MapGet("/tmdb/{kind}/{id:int}", async (HttpContext context, string kind, int id) =>
        {
            var boundary = self!;
            boundary.Calls.Enqueue($"{kind}:{id}");
            var key = context.Request.Query["apikey"].ToString();
            boundary.CallLog.Enqueue(($"{kind}:{id}", Print(key)));
            // Whether the key is right is decided as the call arrives, as MDBList would, not after a test's hold.
            var keyOk = key == boundary.ExpectedKey || (boundary.AlsoAccept is { } also && key == also);
            if (boundary.HoldTitle == id || boundary.HoldAny)
            {
                boundary.Held.TrySetResult();
                await boundary.Release.Task.WaitAsync(TimeSpan.FromSeconds(60));
            }

            var mode = boundary.TitleModes.GetValueOrDefault(id, boundary.Mode);
            if (!keyOk && mode != "errorkey")
            {
                Interlocked.Increment(ref boundary.WrongKeyCalls);
                return Results.Json(new { error = "Invalid API key!" }, statusCode: 401);
            }

            switch (mode)
            {
                case "unauthorized": return Results.Json(new { error = "Invalid API key!" }, statusCode: 401);
                case "errorkey": return Results.Json(new { response = false, error = "Invalid API key!" });
                case "ratelimited":
                    context.Response.Headers.RetryAfter = "120";
                    return Results.Json(new { error = "API limit reached" }, statusCode: 429);
                case "fail": return Results.Text("upstream down", statusCode: 503);
                case "slow":
                    await Task.Delay(TimeSpan.FromSeconds(20), context.RequestAborted);
                    return Results.Json(MdbList(id, kind, true));
                case "malformed": return Results.Text("{\"title\":\"x\",\"ratings\":\"not an array\"", "application/json");
                case "notfound": return Results.Json(new { error = "Not found" }, statusCode: 404);
                case "otherid": return Results.Json(MdbList(id + 1, kind, true));
                // A provider echoing the caller's key in its links (review 2026-10-07, P1, and round 2: in a path, a host name and
                // double-encoded): no link is kept at all, whatever it holds.
                case "keyurl":
                    return Results.Json(new
                    {
                        title = $"Fixture {id}", type = kind, ids = new { tmdb = id },
                        ratings = new object[]
                        {
                            new { source = "imdb", value = 8.1, votes = 250000, url = $"https://www.imdb.com/title/tt{id}/?apikey={key}#frag" },
                            new { source = "letterboxd", value = 4.1, votes = 90000, url = $"https://evil.example/steal?k={key}" },
                            new { source = "trakt", value = 83, votes = 21000, url = $"http://trakt.tv:8080/movies/{id}" },
                            new { source = "tomatoes", value = 91, votes = 310, url = $"https://user:pw@www.rottentomatoes.com/m/{id}" },
                            new { source = "metacritic", value = 74, votes = 52, url = $"https://www.metacritic.com/apikey/{key}" },
                            new { source = "letterboxd", value = 4.1, votes = 90000, url = $"https://letterboxd.com/film/{key}/" },
                            new { source = "trakt", value = 83, votes = 21000, url = $"https://{key}.trakt.tv/movies/test" },
                            new { source = "rogerebert", value = 3.5, url = $"https://www.rogerebert.com/%2561pikey/{key}" }
                        }
                    });
                // Structurally wrong ratings: a numeric source (review 2026-10-07, P2 5).
                case "badstructure": return Results.Text("{\"ids\":{\"tmdb\":" + id + "},\"ratings\":[{\"source\":42,\"value\":8.1}]}", "application/json");
                case "badvalue": return Results.Text("{\"ratings\":[{\"source\":\"imdb\",\"value\":{\"x\":1}}]}", "application/json");
                // A score of the wrong kind beside no value at all (review 2026-10-07 round 2, P2 6).
                case "badscore": return Results.Text("{\"ratings\":[{\"source\":\"imdb\",\"score\":{\"x\":1}}]}", "application/json");
                // Absent values MDBList is known to send: still a valid answer.
                case "absent": return Results.Text("{\"ratings\":[{\"source\":\"imdb\",\"value\":\"N/A\",\"votes\":\"\"},{\"source\":\"tomatoes\",\"value\":null},{\"source\":\"trakt\",\"value\":\"77\",\"votes\":\"1200\"}]}", "application/json");
                // A short Retry-After beside a reset two days away (review 2026-10-07, P2 6).
                case "bothheaders":
                    context.Response.Headers.RetryAfter = "60";
                    context.Response.Headers["X-RateLimit-Reset"] = boundary.ResetAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return Results.Json(new { error = "API limit reached" }, statusCode: 429);
                // A quota reset more than a year away (review 2026-10-07 round 2, P3 7).
                case "longretry":
                    context.Response.Headers.RetryAfter = "40000000";
                    return Results.Json(new { error = "API limit reached" }, statusCode: 429);
                // A delay too large for .NET's own header parser (review round 3, P3 6).
                case "hugeretry":
                    context.Response.Headers.RetryAfter = "1000000000000";
                    return Results.Json(new { error = "API limit reached" }, statusCode: 429);
                case "partial": return Results.Json(MdbList(id, kind, false));
                default: return Results.Json(MdbList(id, kind, true));
            }
        });
        app.MapGet("/3/movie/{id:int}", (int id) => Results.Json(new
        {
            id, title = $"JellyfinMod Ratings Movie {id}", release_date = "2026-01-02", overview = "A disposable fixture.",
            vote_average = 7.8, vote_count = 1234, genres = Array.Empty<object>(), external_ids = new { imdb_id = $"tt{id}" },
            release_dates = new { results = Array.Empty<object>() }, adult = false
        }));
        // A series whose TMDB answer carries no score, like a title the native backfill created.
        app.MapGet("/3/tv/{id:int}", (int id) => Results.Json(new
        {
            id, name = $"JellyfinMod Ratings Series {id}", first_air_date = "2025-01-01", overview = "A disposable fixture.",
            seasons = Array.Empty<object>(), external_ids = new { tvdb_id = (int?)null }, content_ratings = new { results = Array.Empty<object>() },
            genres = Array.Empty<object>(), adult = false
        }));
        await app.StartAsync();
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        self = new Boundary(app, address);
        return self;
    }

    /// <summary>The MDBList answer: every source MDBList names, plus one this release does not know; partial drops the audience score.</summary>
    private static object MdbList(int id, string kind, bool full)
    {
        var ratings = new List<object>
        {
            new { source = "imdb", value = 8.1, score = 81, votes = 250000, url = $"https://www.imdb.com/title/tt{id}/" },
            new { source = "metacritic", value = 74, score = 74, votes = 52, url = (string?)null },
            new { source = "metacriticuser", value = 8.4, score = 84, votes = 1300, url = (string?)null },
            new { source = "trakt", value = 83, score = 83, votes = 21000, url = (string?)null },
            new { source = "tomatoes", value = 91, score = 91, votes = 310, url = (string?)null },
            new { source = "letterboxd", value = 4.1, score = 82, votes = 90000, url = (string?)null },
            new { source = "rogerebert", value = 3.5, score = 88, votes = (int?)null, url = (string?)null },
            new { source = "tmdb", value = 79, score = 79, votes = 15000, url = (string?)null },
            new { source = "myanimelist", value = 7.2, score = 72, votes = 10, url = (string?)null },
            // A source MDBList has no score for yet: never stored, never shown as zero.
            new { source = "metacriticuser2", value = 0, score = 0, votes = 0, url = (string?)null }
        };
        if (full) ratings.Add(new { source = "popcorn", value = 88, score = 88, votes = 25000, url = (string?)null });
        return new
        {
            title = $"Fixture {id}", year = 2026, type = kind, ids = new { imdb = $"tt{id}", tmdb = id, trakt = 1, tvdb = (int?)null },
            score = 82, score_average = 82, ratings
        };
    }

    public async ValueTask DisposeAsync()
    {
        Release.TrySetResult();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal static class Json
{
    public static JsonElement Parse(string text) => text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
}
