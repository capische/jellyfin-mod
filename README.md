# JellyfinMod

![JellyfinMod](JellyfinMod/Assets/logo.png)

Plugin loading, authenticated health, configuration, persistent SQLite and the Phase 1 catalog
APIs are implemented. Phase 2 positive reconciliation has local integration coverage and has
passed its initial native-host backfill/idempotency checkpoint. Disappearance handling and the
remaining browser acceptance are still incomplete. Acquisition and retention are planned work.

The server half of **JellyfinMod**. The other half is the
[`jellyfin-web`](https://github.com/capische/jellyfin-web) fork.

A title is **one entry** that may or may not have a media file behind it yet. The entry lives here,
in this plugin's own SQLite database; it is never a Jellyfin `BaseItem`. That single decision is
what makes a plugin sufficient and a server fork unnecessary.

Read `../jellyfin-web/docs/jellyfinmod/README.md` for the architecture and
`../jellyfin-web/docs/jellyfinmod/UX.md` for the interface it has to serve. Both carry dated
decision logs; several questions in them are settled.

## Target

Pinned to the server actually running on the Pi.

| | |
| --- | --- |
| Framework | `net9.0` |
| Packages | `Jellyfin.Controller` / `Jellyfin.Model` `10.11.11` |
| `targetAbi` | `10.11.0.0` |

There is no Jellyfin 11.x — versions ran 10.x to 10.11, then dropped the leading `10.`, so 10.12
became 12.0. Moving to 12.0 / net10 is a deliberate later step, not drift.

## Layout

Follows the official
[`jellyfin-plugin-template`](https://github.com/jellyfin/jellyfin-plugin-template): the project
folder sits at the repo root (no `src/`), `Directory.Build.props` carries the settings every
project shares, and **`build.yaml` is the manifest**. `meta.json` is *generated* from it by
[JPRM](https://github.com/oddstr13/jellyfin-plugin-repository-manager) at package time — do not
hand-write one.

```
plugin/
  build.yaml                 the manifest JPRM reads
  Directory.Build.props      shared build settings
  JellyfinMod/               the project; folder name = csproj name = root namespace
    Plugin.cs
    PluginServiceRegistrator.cs
    Configuration/           PluginConfiguration.cs + configPage.html
    Data/                    Entry.cs, HistoryRecord.cs, ModDbContext.cs
    Api/                     HealthController.cs
```

The template's convention is `Jellyfin.Plugin.<Name>`. This deviates to plain `JellyfinMod`: the
convention is a convention, not a requirement, and `Jellyfin.Plugin.JellyfinMod` stutters.

## Build

```bash
dotnet build -c Release JellyfinMod/JellyfinMod.csproj
```

Package the compiled assembly and plugin-card logo with the JPRM-based helper:

```bash
python3 -m venv .venv
.venv/bin/pip install -r scripts/requirements.txt
.venv/bin/python scripts/package_plugin.py JellyfinMod/bin/Release/net9.0 artifacts/package
```

Copy all three generated files (`JellyfinMod.dll`, `logo.png`, `meta.json`) from `artifacts/package`
into the plugin's own folder under the server's persistent `plugins/` directory, then restart
Jellyfin. The helper reads `build.yaml` and adds the `imagePath` field required by Jellyfin 10.11;
copying only the DLL leaves the installed plugin card without its logo.
The assembly version matches `build.yaml` (`0.1.0.0`).
Jellyfin 10.11 already supplies EF Core 9 and SQLite, including the native SQLite library; do
not copy the host assemblies or a second database stack into the plugin directory.

For plugin-only deployment from the sibling web checkout:

```bash
JELLYFIN_PLUGIN_PYTHON=../plugin/.venv/bin/python ./jellyfin-sync --local --plugin
```

See `jellyfin-sync.env.example` for project, destination and SDK path overrides. This leaves
the web bundle untouched. `--no-build` packages the existing Release DLL; `--no-restart` only
copies the package. Existing enable/disable and automatic-update choices are preserved.

## Database and local validation

Startup applies pending EF migrations at `Plugin.Instance.DataPath/jellyfinmod.db`. A database
failure is logged and makes Health return 503, while Jellyfin continues starting. Signed-in
users receive 200 once the database is ready; anonymous requests require authentication.

```bash
dotnet tool restore
dotnet ef migrations has-pending-model-changes --project JellyfinMod/JellyfinMod.csproj
dotnet run -c Release --project tests/PhaseZeroSmoke/PhaseZeroSmoke.csproj
```

The smoke test creates a temporary SQLite database, checks migration and row persistence after
initialization runs twice, checks failure health, and round-trips XML configuration. It does not
replace installation, authentication and Dashboard checks against a running Jellyfin server.

## Phase 2 validation checkpoint

R1–R3 reconciliation reads native titles in pages of at most 50, re-reads a title while holding
its library lease, and commits each title in a separate service scope. Episode observations use
physical ancestry and the episode's own `SeriesId`; they do not use the grouped recursive
`Series.GetItemList` query. A concurrent user add completes its fetched episode set after a
backfill wins the entry race. These operations only record positive observations.

The local suites passed at the 2026-09-13 review checkpoint:

```bash
dotnet run --project tests/PhaseOneSmoke/PhaseOneSmoke.csproj
dotnet run --project tests/PhaseTwoIntegration/PhaseTwoIntegration.csproj
```

- The first backfill fixture processes 57 logical titles: 53 created, two unmatched, one
  conflicted and one failed. Its rerun has 53 unchanged titles and creates no new history.
- An injected item failure preserves all counters: 52 unchanged, two unmatched, one conflicted
  and two failed. Cancellation persists the first completed item before another page is read.
- Grouped series copies preserve three native episode bindings across two durable episodes.
  An unidentified series with incomplete episode numbering does not abort the run.
- Queued work does not overwrite a newer event's replacement binding. Item notifications do
  not enumerate all virtual folders. Repeated events do not duplicate transition history.
- The HTTP series-add race retains the complete monitored episode set, the backfilled pilot's
  local/native IDs, and one creation event plus one monitoring event.

The HTTP suite runs real Kestrel, authentication/authorization middleware, serialization, a TMDB
HTTP boundary and SQLite migrations/transactions. Its users and native library are controlled
fixtures. The Phase 2 suite uses the production DI registrations and pinned native entity types,
but `ILibraryManager` is still a fixture. Neither suite proves behavior inside a running Jellyfin
server or replaces the required browser and native-host acceptance.

Uncommitted R4 preparation remains outside the validated commit set. The validation source copy
excluded its changes to `ReconciliationRun.cs`, `ReconciliationContracts.cs`, `configPage.html`,
and the storage-observation helpers in `JellyfinNativeTitleSource.cs`. The prepared model fields
have no migration yet, so running EF migrations with those dirty files reports pending model
changes; no warning was suppressed and no R4 migration was invented.

The isolated `jellyfinmod-test` host completed the first native-library run on 2026-09-14. Jellyfin
12 exposes video version identities as GUIDs and the deployed Phase 2 binding migration predated
its provenance columns. Compatibility readers and a forward-only provenance migration were added,
then validated against a copy of the deployed SQLite database before redeploying to the test host.

The successful backfill scanned all 159 native title observations in 7.3 seconds: 157 entries were
created, one item without a usable TMDB identity was unmatched, and one series with conflicting
episode provenance failed independently. The immediate rerun scanned the same 159 observations:
157 were unchanged, zero entries or bindings were created or updated, and the same two bounded
diagnostics remained. The durable database then contained 160 entries, 157 entry bindings and 160
history records, with no duplicate library-scoped entries or bindings. Startup and migration logs
contained neither the previous missing-method failure nor a missing-column failure.

These results prove positive backfill and idempotency on the isolated native host. Before Phase 2
acceptance, still exercise real grouped copies, provider corrections, replacement events overlapping
repair, cancellation/rerun, and ordinary-user adds during backfill. Verify exact persisted
counts/history, library access, native playback and stable entry bookmarks. R4 still requires
proven successful scan completion and available storage before any missing-media transition. R5
still requires two-user state/access checks and desktop/mobile/TV browser acceptance. No
disappearance handling, production deployment or production data changes are claimed here.

## Phase 0 — done when

The plugin loads, `GET /JellyfinMod/Health` answers for a signed-in user, the config page appears
under Dashboard → Plugins, and it survives a container restart.

## Licensing

Jellyfin is **GPL-2.0-only**; Sonarr and Radarr are GPL-3.0 and therefore incompatible. Do not port
their release parsers. Use an MIT-licensed reference (`dreulavelle/PTT`,
`clement-escolano/parse-torrent-title`) or write regexes against Sonarr's *test cases* — behaviour
is not copyrightable, source is.
