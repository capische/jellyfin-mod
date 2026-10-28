using System.ComponentModel.DataAnnotations;

namespace JellyfinMod.Data;

/// <summary>The administrator's ratings settings (P9.R2). One row; the MDBList key lives in the secret store.</summary>
public class RatingsSettings
{
    /// <summary>The singleton row's id.</summary>
    public static Guid SingletonId { get; } = Guid.Parse("4c1f7a3e-9b2d-4e60-8a15-2f9d0c7b6e31");

    /// <summary>
    /// The default sources shown, in order (user decision 9, 2026-10-08: IMDb, Rotten Tomatoes critics and audience, Trakt; every
    /// other source off). It replaced decision 6's list, which also had TMDB.
    /// </summary>
    public const string DefaultSourcesJson = "[\"imdb\",\"tomatoes_critic\",\"tomatoes_audience\",\"trakt\"]";

    /// <summary>Decision 6's default (2026-10-07), which migration <c>PhaseNineRatingsDisplayDefaults</c> replaces where it was never changed.</summary>
    public const string FirstDefaultSourcesJson = "[\"imdb\",\"tomatoes_critic\",\"tomatoes_audience\",\"tmdb\",\"trakt\"]";

    /// <summary>Gets or sets the primary key.</summary>
    public Guid Id { get; set; } = SingletonId;

    /// <summary>Gets or sets a value indicating whether ratings are fetched and shown at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the secret-store reference of the MDBList API key.</summary>
    [MaxLength(64)]
    public string? ApiKeyRef { get; set; }

    /// <summary>Gets or sets how many days a title's ratings stay current before they are fetched again.</summary>
    public int RefreshDays { get; set; } = 14;

    /// <summary>Gets or sets the most MDBList calls made per UTC day, manual refreshes and Tests included.</summary>
    public int DailyBudget { get; set; } = 500;

    /// <summary>Gets or sets the administrator's default source order as a JSON array of source names.</summary>
    [MaxLength(512)]
    public string DefaultSources { get; set; } = DefaultSourcesJson;

    /// <summary>Gets or sets the revision every change increments.</summary>
    public int Revision { get; set; } = 1;

    /// <summary>Gets or sets the revision whose key last passed Test.</summary>
    public int? VerifiedRevision { get; set; }

    /// <summary>Gets or sets when the key last passed Test.</summary>
    public DateTime? VerifiedAt { get; set; }
}

/// <summary>What the fetcher knows about the provider right now (P9.R3): blocker, breaker, today's budget, last run.</summary>
public class RatingsProviderState
{
    /// <summary>The singleton row's id.</summary>
    public static Guid SingletonId { get; } = Guid.Parse("9e2b5d41-7c3a-4f18-b6e0-5a4d8c1f2e97");

    /// <summary>Gets or sets the primary key.</summary>
    public Guid Id { get; set; } = SingletonId;

    /// <summary>Gets or sets a blocker that stops all fetching until an administrator acts (<c>unauthorized</c>).</summary>
    [MaxLength(32)]
    public string? Blocker { get; set; }

    /// <summary>Gets or sets when the open breaker closes again; null when closed.</summary>
    public DateTime? BreakerUntil { get; set; }

    /// <summary>Gets or sets why the breaker opened (<c>rate_limited</c> or <c>failures</c>).</summary>
    [MaxLength(32)]
    public string? BreakerReason { get; set; }

    /// <summary>Gets or sets the consecutive transient failures since the last success.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>Gets or sets the UTC day <see cref="BudgetUsed"/> counts.</summary>
    public DateTime? BudgetDay { get; set; }

    /// <summary>Gets or sets the MDBList calls made on <see cref="BudgetDay"/>.</summary>
    public int BudgetUsed { get; set; }

    /// <summary>Gets or sets when the last scheduled or manual run started.</summary>
    public DateTime? LastRunStartedAt { get; set; }

    /// <summary>Gets or sets when the last run finished.</summary>
    public DateTime? LastRunFinishedAt { get; set; }

    /// <summary>Gets or sets the titles the last run fetched successfully.</summary>
    public int LastRunFetched { get; set; }

    /// <summary>Gets or sets the titles the last run failed to fetch.</summary>
    public int LastRunFailed { get; set; }

    /// <summary>Gets or sets why the last run stopped early, or null when it finished its due list.</summary>
    [MaxLength(32)]
    public string? LastRunStopReason { get; set; }
}

/// <summary>One rating of one title from one source and provider, in the source's own scale (P9.R2).</summary>
public class TitleRating
{
    /// <summary>Gets or sets the primary key.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the catalog entry.</summary>
    public Guid EntryId { get; set; }

    /// <summary>Gets or sets the source (<c>imdb</c>, <c>tomatoes_critic</c>, …), or a raw MDBList name.</summary>
    [MaxLength(32)]
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets who supplied it (<c>mdblist</c>).</summary>
    [MaxLength(16)]
    public string Provider { get; set; } = string.Empty;

    /// <summary>Gets or sets the value in its own scale.</summary>
    public double Value { get; set; }

    /// <summary>Gets or sets <c>ten</c>, <c>percent</c>, <c>five</c>, <c>four</c> or <c>unknown</c>.</summary>
    [MaxLength(8)]
    public string Scale { get; set; } = string.Empty;

    /// <summary>Gets or sets the vote count when the provider gives one.</summary>
    public int? Votes { get; set; }

    /// <summary>Gets or sets when it was fetched.</summary>
    public DateTime FetchedAt { get; set; }

    // No provider link is kept (review 2026-10-07 round 2, P1): a link the provider writes can carry anything, the key
    // included, in its path or host, and nothing in JellyfinMod shows one.
}

/// <summary>
/// The latest ratings fetch attempt for one title identity (P9.R3): one row per media type and TMDB id, however many entries
/// (libraries) hold the title, so it is bounded. It is not tied to an entry: removing the entry that was being fetched does not
/// remove the attempt, so a crash mid-call still counts against the title when another library holds it (review 2026-10-07
/// round 2, P2 5). A row whose title no entry holds any more is pruned once its retry window has passed.
/// </summary>
public class RatingsFetch
{
    /// <summary>Gets or sets the primary key.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the title's media type: movie or series.</summary>
    [MaxLength(16)]
    public string MediaType { get; set; } = "movie";

    /// <summary>Gets or sets the title's TMDB id.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets when the attempt was claimed.</summary>
    public DateTime AttemptedAt { get; set; }

    /// <summary>Gets or sets the outcome; <c>pending</c> while the call is in flight.</summary>
    [MaxLength(16)]
    public string Outcome { get; set; } = RatingsOutcomes.Pending;

    /// <summary>Gets or sets a short administrator-only description; never a key, URL or body.</summary>
    [MaxLength(200)]
    public string? Error { get; set; }

    /// <summary>Gets or sets a value indicating whether an administrator asked for this attempt.</summary>
    public bool Manual { get; set; }
}

/// <summary>The stable outcome codes of a ratings fetch or Test.</summary>
public static class RatingsOutcomes
{
    /// <summary>Claimed; the call has not finished.</summary>
    public const string Pending = "pending";

    /// <summary>Ratings arrived (possibly only some sources).</summary>
    public const string Ok = "ok";

    /// <summary>The provider does not know the title.</summary>
    public const string NotFound = "not_found";

    /// <summary>The key was refused.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>The provider's quota is spent (429).</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>The provider answered with a server error.</summary>
    public const string Failed = "failed";

    /// <summary>The provider could not be reached.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>The provider did not answer in time.</summary>
    public const string Timeout = "timeout";

    /// <summary>The provider's answer could not be read.</summary>
    public const string Malformed = "malformed";

    /// <summary>A claim that never finished (the process stopped mid-call); counted as a failed attempt.</summary>
    public const string Interrupted = "interrupted";

    /// <summary>No key is configured.</summary>
    public const string NotConfigured = "not_configured";

    /// <summary>The database stayed busy past the fetcher's bound, so the step did not happen or was not recorded.</summary>
    public const string DatabaseBusy = "database_busy";
}
