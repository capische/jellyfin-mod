# JellyfinMod

Server half of JellyfinMod. The web half is the `jellyfin-web` fork beside this repo; read
`../jellyfin-web/docs/jellyfinmod/README.md` (architecture, data model, roadmap) and
`../jellyfin-web/docs/jellyfinmod/UX.md` (the interface this has to serve) before any work here.
Both carry dated decision logs — several questions are settled and should not be reopened without
new evidence.

Tasks live in `../jellyfin-web/docs/jellyfinmod/PLAN.md`; plugin tasks are prefixed **P**. Start
there rather than inventing a task order.

## The one idea

**A title is one entry that may or may not have a media file behind it.** Entries live in this
plugin's SQLite database and are never Jellyfin `BaseItem`s. There is no separate "catalog" — the
web fork shows these entries inside the existing Movies and TV libraries.

## Target, pinned

`net9.0` · `Jellyfin.Controller`/`Jellyfin.Model` `10.11.11` · `targetAbi` `10.11.0.0`, matching the
server on the Pi. There is no Jellyfin 11.x. Moving to 12.0 / net10 is a deliberate later step.

## Layout

Mirrors the official `jellyfin-plugin-template`: project folder at the repo root (no `src/`),
`Directory.Build.props` for shared settings, and **`build.yaml` as the manifest** — `meta.json` is
generated from it by JPRM, never hand-written.

## Rules

- **Plugin config is XML** (`XmlSerializer`), so no `Dictionary<,>` in `PluginConfiguration`.
  Anything structured goes in the database.
- **Never trust `BasePlugin.DataFolderPath`** for the database — it can gain a `_<Version>` suffix
  across upgrades and orphan the data. Use `Plugin.Instance.DataPath`.
- `IServerEntryPoint` was removed in 10.9. Use `IScheduledTask` for periodic work and
  `IHostedService` for long-lived loops.
- Some host services are scoped, not singleton — do not capture them in a singleton.
- Use `IHttpClientFactory.CreateClient(NamedClient.Default)`.
- **Never delete a file the torrent client is still seeding** under its ratio or time goal, and
  never count a hardlinked file as reclaimed space without checking the link count.
- Imports **hardlink**; a copy doubles every file while it seeds. Download dir and library must
  share a filesystem.
- **Licensing: Jellyfin is GPL-2.0-only, Sonarr/Radarr are GPL-3.0 — incompatible.** Do not port
  their parsers. MIT references only, or regexes written against their *test cases*.

## API shape

Everything under `/JellyfinMod`, `[Authorize]`, admin-only where it changes server config
(`[Authorize(Policy = Policies.RequiresElevation)]`). There is no `Policies.DefaultAuthorization`.

## Commits

Commit validated slices regularly. Use Conventional Commits with the phase/task ID in the
scope: `feat(P2.R1): reconcile native library bindings` or `fix(P1.P6): correct tmdb requests`.
Use task IDs from the plan; use a lower-case imperative description without a trailing full stop.
For existing combined commits, list their tasks, such as `P1.P5,P1.P6`; prefer separate task
commits for new work. Never add a Codex/GPT co-author or commit secrets. Rewrite published
history only when explicitly authorized, using a verified remote tip and explicit
force-with-lease, then verify the pushed commit.
