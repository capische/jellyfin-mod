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

    /// <summary>When set, a call for this title waits until <see cref="Release"/> completes.</summary>
    public int? HoldTitle { get; set; }

    public TaskCompletionSource Held { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ResetHold()
    {
        Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HoldTitle = null;
    }

    public int CallsFor(string kind, int id) => Calls.Count(call => call == $"{kind}:{id}");

    public static async Task<Boundary> StartAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddRouting();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        Boundary? self = null;
        app.MapGet("/tmdb/{kind}/{id:int}", async (HttpContext context, string kind, int id) =>
        {
            var boundary = self!;
            boundary.Calls.Enqueue($"{kind}:{id}");
            var key = context.Request.Query["apikey"].ToString();
            if (boundary.HoldTitle == id)
            {
                boundary.Held.TrySetResult();
                await boundary.Release.Task.WaitAsync(TimeSpan.FromSeconds(60));
            }

            var mode = boundary.TitleModes.GetValueOrDefault(id, boundary.Mode);
            if (key != boundary.ExpectedKey && mode != "errorkey")
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
