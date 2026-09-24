# JellyfinMod

![JellyfinMod](JellyfinMod/Assets/logo.png)

Plugin loading, authenticated health, configuration, persistent SQLite and the Phase 1 catalog
APIs are implemented. Phase 2 reconciliation has passed its integration, native-host and browser
acceptance checkpoints. Phase 3 T1 policy, completion evidence and access-aware deadline evaluation
are implemented and accepted on the isolated test instance. Retention preview, protection checks and
reclamation remain planned work; no automatic file deletion exists yet.

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

`targetAbi` is a minimum: this build runs on the Jellyfin 12.0.0 server the Docker image pins, and
that is the only host it has been tested on. The compatibility matrix is PHASE7 §3.5.

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

## Distribution

Two shapes (PHASE7 §4.8). Both are built from one release directory:

```bash
# In the jellyfin-web fork, on jellyfin-mod: npm ci && npm run build:production
JELLYFIN_PLUGIN_PYTHON=.venv/bin/python scripts/build-release.sh --web-dist ../jellyfin-web/dist
```

`artifacts/release/` then holds `package/` (the plugin with `jellyfinmod-web.zip`), the release
archive `jellyfinmod-<version>.zip` and `image/`, the Docker build context. Nothing in the image
build compiles; the assembly is architecture-independent, so build the image on the host that runs
it (or with `buildx` for another platform).

### Docker image (preferred)

```bash
docker build -t capische/jellyfinmod:<version> artifacts/release/image
```

```yaml
services:
  jellyfin:
    image: capische/jellyfinmod:<version>
    user: "1000:1000"
    ports: ["8096:8096"]
    volumes:
      - ./config:/config
      - ./cache:/cache
      - /path/to/media:/media:ro
    # environment:
    #   JELLYFINMOD_UI_TAKEOVER: "false"   # first start only: keep the stock page at /web
```

The image is the pinned `jellyfin/jellyfin` digest (Jellyfin 12.0.0) plus the plugin at
`/opt/jellyfinmod/plugin/`. Its entrypoint runs before Jellyfin and then `exec`s it:

- **Plugin install.** Copies the plugin into `/config/plugins/JellyfinMod_<version>/` when it is
  missing or older than the image's. It never downgrades and never touches another plugin; an
  administrator's *Disabled* status is carried over to a newer version.
- **Repository, preconfigured once.** Registers *JellyfinMod (this server)* at
  `http://localhost:8096/JellyfinMod/Repository` — the server's own address, so nothing external is
  contacted — which is what gives Dashboard → Plugins a details panel and an update path instead of
  a repository error. It is image configuration, not plugin behaviour: the plugin never edits the
  repository list. It is added once per config volume (a marker in `/config/jellyfinmod-image/`),
  so removing it in Manage Repositories is permanent; it is not added if a JellyfinMod repository is
  already registered. On an empty config the entrypoint lets Jellyfin's first start finish before
  adding it, because a fresh server's setup migrations replace the whole repository list; that
  costs one extra start-up (about 20 s on a Raspberry Pi), once.
- **Takeover.** The plugin replaces `/web/index.html` in the container's own web directory at every
  start (on by default, decision 7). The Dockerfile hands only that directory to `1000:1000`; run as
  that user or as root. Any other user starts cleanly with the takeover blocked
  (`web_root_read_only`) and the interface at `/web-mod/`. `JELLYFINMOD_UI_TAKEOVER=false` is read
  once, before the plugin has a configuration; afterwards the switch is Settings → Interface.

**Upgrade:** pull or build the new tag and `docker compose up -d`. The new plugin is installed beside
the old one, Jellyfin keeps the newest, and the web directory — stock again in the new container —
is re-patched with the new bundle at startup.
**Rollback:** an older image tag rolls back Jellyfin, not the plugin: a newer plugin already in the
config volume stays, because its database migrations are forward-only. To run an older plugin,
remove `/config/plugins/JellyfinMod_<newer>/` with the container stopped, knowing the database
may carry migrations that version does not know.
**Manual recovery** if `/web` is ever broken: recreate the container (its web directory is stock
again), or copy `index.jellyfinmod-stock.html` over `index.html` in the web directory, or copy
`/config/data/jellyfinmod/web-root/index.html.pristine`. None of these needs the Dashboard.

### Release archive (secondary)

`jellyfinmod-<version>.zip` holds `plugin/` and `web/`. Copy `plugin/` into
`<config>/plugins/JellyfinMod_<version>/` of a stock server and restart: the takeover works as in
the image wherever the web directory is writable. Or also point `JELLYFIN_WEB_DIR` (or a volume) at
`web/`: the host then serves the fork's own build, the plugin reports `forkServedByHost` and writes
nothing, `web/index.html` is the fork's stock entry and `web/jellyfinmod.html` the JellyfinMod entry.
In that shape "disable → stock" is the operator's own swap back to the host's web directory.

A manual install registers no repository; add `<your-server>/JellyfinMod/Repository` in Dashboard →
Plugins → Manage Repositories to get the details panel and the update path.

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

R1–R4 reconciliation reads native titles in pages of at most 50, re-reads a title while holding
its library lease, and commits each title in a separate service scope. Episode observations use
physical ancestry and the episode's own `SeriesId`; they do not use the grouped recursive
`Series.GetItemList` query. A concurrent user add completes its fetched episode set after a
backfill wins the entry race.

Only the post-scan task confirms absence. It requires two identical complete observations while
holding the library lease. Each positive title and episode binding records its native media path
and Linux mount identity. An absent binding is cleared only when that same mount still contains
the configured readable library root; bindings without this provenance remain intact until a
positive reconciliation records it. Manual and scheduled repair runs remain positive-only.

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
- The absence fixture covers a changing collection larger than one page, a disappeared nested
  mount with a stale nonempty directory, surviving copies, episode-only removal and return,
  stable catalog IDs, transition history and idempotent reruns.

The HTTP suite runs real Kestrel, authentication/authorization middleware, serialization, a TMDB
HTTP boundary and SQLite migrations/transactions. Its users and native library are controlled
fixtures. The Phase 2 suite uses the production DI registrations and pinned native entity types,
but `ILibraryManager` is still a fixture. Neither suite proves behavior inside a running Jellyfin
server or replaces the required browser and native-host acceptance.

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

These results prove positive backfill and idempotency on the isolated native host. The R4 migration
also preserves all entry, binding, episode and history counts when applied to a copy of that
database and passes SQLite integrity checking.

The isolated R4 movie fixture completed a native scan/removal/return run on 2026-09-15. Entry
`efe27ec9dffe405d8c97a6c23e324cab` changed from `onDisk` to `none` and back to `onDisk`, retained
its durable identity/history, and rebound to native item `ba9815a6b64dfb9820639f36f8271a1a`.
The TV library card refreshed in both directions after scan completion without reloading or losing
focus.

R5 then passed isolated browser acceptance using web commit `4c465f8f37`: one reconciled global
search result, stable `entryId` bookmark redirection, native playback target, desktop/mobile/TV
layouts, TV D-pad navigation and Poster/List views. A temporary non-admin user saw its permitted
movie library but not the restricted TV library; changing that user's played state did not change
the admin user's state. The temporary account was removed after the check.

The remaining native-host scenarios passed on the isolated server on 2026-09-15:

- Two physical copies with TMDB 550 produced one durable catalog entry with two bindings. Removing
  one copy retained the card, rebound it to the survivor and did not emit `media_missing`.
- Overlapping `RefreshLibrary` and `JellyfinModCatalogReconciliation` tasks converged to the restored
  native item with no duplicate entry or binding. The repair summary scanned 161 observations, had
  160 unchanged and one unmatched item, and reported zero failures or conflicts.
- Cancelling reconciliation after three committed observations persisted a bounded partial run.
  Its immediate rerun scanned all 161 observations and again reported 160 unchanged, one unmatched,
  and zero created, updated, conflicted or failed rows.
- A non-admin add for TMDB 552 raced a full reconciliation. Backfill won the insert, the request
  returned the same single entry with monitoring enabled, and history contained one backfill plus
  one monitoring transition.
- A native movie without a provider ID remained unmatched. After its native metadata was corrected
  to TMDB 553, the next scan created one bound `onDisk` entry without title-based guessing.

All disposable media, entries and the temporary user were removed. The database returned to 163
entries and 160 bindings; the TMDB 550 fixture retained one binding to its original C copy. The
four expected test users remained. Phase 2 native and browser acceptance is complete on
`jellyfinmod-test`. No production deployment or production data changes are claimed.

## Phase 3 T1 validation checkpoint

T1 persists the disabled-by-default All/Selected/Any policy, per-user movie and episode completion
evidence, and access-aware deadline evaluations. Native user-data and user-policy callbacks enqueue
work and re-read authoritative Jellyfin state outside the callback. The scheduled evidence repair
recovers missed events. Evaluations retain the policy revision, a non-identifying access-set hash,
completion basis, full-grace start and deadline. They never delete files.

The integration suite covers duplicate and out-of-order notifications, replay/resume, unwatched,
favorites, missing evidence, Keep, per-entry days, restart persistence, re-enable baseline grace,
deadline non-shortening and access membership changes for All, Selected and Any user modes. All
Phase 0–3 integration suites pass, and the eighth migration preserved the isolated database copy's
163 entries, 160 bindings, 22 episodes and 224 completion observations with SQLite integrity and
foreign-key checks clean.

The isolated native-host check then produced 224 observations for 56 bound movie/episode targets
and four users. Real watched, unwatched and favorite events updated only the affected user/target
row. The browser configuration page persisted all three modes and the selected-user picker across
reloads. Selected and Any scheduled the disposable R4 movie after `oleksii` watched it; All waited
for the other accessible users. The movie finished unwatched and unfavorited, retention was restored
to disabled/All users, and all 56 evaluations returned to `retention_disabled`. Production was not
deployed or modified.

## Phase 0 — done when

The plugin loads, `GET /JellyfinMod/Health` answers for a signed-in user, the config page appears
under Dashboard → Plugins, and it survives a container restart.

## Licensing

Jellyfin is **GPL-2.0-only**; Sonarr and Radarr are GPL-3.0 and therefore incompatible. Do not port
their release parsers. Use an MIT-licensed reference (`dreulavelle/PTT`,
`clement-escolano/parse-torrent-title`) or write regexes against Sonarr's *test cases* — behaviour
is not copyrightable, source is.
