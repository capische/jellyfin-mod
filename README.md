# JellyfinMod

![JellyfinMod](JellyfinMod/Assets/logo.png)

Phase 0 foundation: plugin loading, authenticated health, configuration and persistent SQLite
storage are implemented. Catalog/search, acquisition and retention are planned work.

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

For a release package, use JPRM (`jprm plugin build .`), which reads `build.yaml` and emits the
zip plus its `meta.json`. To test by hand, copy `JellyfinMod.dll` into a folder under the server's
`plugins/` directory and restart Jellyfin. The assembly version matches `build.yaml` (`0.1.0.0`).
Jellyfin 10.11 already supplies EF Core 9 and SQLite, including the native SQLite library; do
not copy the host assemblies or a second database stack into the plugin directory.

For plugin-only deployment from the sibling web checkout:

```bash
./jellyfin-sync --local --plugin
```

See `jellyfin-sync.env.example` for project, destination and SDK path overrides. This leaves
the web bundle untouched. `--no-build` ships the existing Release DLL; `--no-restart` only copies it.

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

## Phase 0 — done when

The plugin loads, `GET /JellyfinMod/Health` answers for a signed-in user, the config page appears
under Dashboard → Plugins, and it survives a container restart.

## Licensing

Jellyfin is **GPL-2.0-only**; Sonarr and Radarr are GPL-3.0 and therefore incompatible. Do not port
their release parsers. Use an MIT-licensed reference (`dreulavelle/PTT`,
`clement-escolano/parse-torrent-title`) or write regexes against Sonarr's *test cases* — behaviour
is not copyrightable, source is.
