# JellyfinMod plugin rules

## Scope and planning

- Read workspace rules and `../jellyfin-web/docs/jellyfinmod/{PLAN,README,UX}.md`, plus the current phase refinement.
- Follow the plan's task order and accepted decisions. Preserve other agents' edits.
- Store library-scoped catalog entries in plugin SQLite, never as synthetic Jellyfin `BaseItem`s.
- Use existing Movies/TV views; do not introduce a separate catalog section.

## Target and packaging

- Target `net10.0`, Jellyfin Controller/Model `12.0.0`, EF Core `10.0.11` (the host's own), and `targetAbi: 12.0.0.0`.
- Jellyfin 10.11 is no longer supported (user decision, 2026-09-24). Do not change these pins to bypass build errors; a host upgrade is a deliberate retarget.
- Keep the project at the repository root, shared settings in `Directory.Build.props`, and manifest in `build.yaml`.
- Generate `meta.json` through JPRM; never hand-write it.

## Implementation

- Keep plugin configuration XML-serializable; do not put `Dictionary<,>` in `PluginConfiguration`. Store structured data in SQLite.
- Use `Plugin.Instance.DataPath` for the DB; never rely on the version-dependent `BasePlugin.DataFolderPath`.
- Use `IScheduledTask` for periodic tasks and `IHostedService` for long-running work; never use removed `IServerEntryPoint`.
- Resolve scoped services within scopes; never capture them in singletons.
- Use `IHttpClientFactory.CreateClient(NamedClient.Default)`.
- Put APIs under `/JellyfinMod`; require `[Authorize]` and `Policies.RequiresElevation` for admin-only writes. Do not use nonexistent `Policies.DefaultAuthorization`.
- Enforce accepted permissions: users may add accessible titles; only admins remove entries or change settings.
- Preserve seed ratio/time goals. Check hardlink counts before claiming reclaimed disk space.
- Import with hardlinks; require download and library paths on the same filesystem.
- Do not port GPL-3.0 Sonarr/Radarr parsers into this GPL-2.0-only project. Use MIT references or independently written parsers.

## Testing and deployment

- Use real HTTP/auth/serializer/SQLite integration and browser E2E; do not add unit tests.
- Require E2E acceptance; a successful build alone is insufficient.
- Deploy mod work only to the isolated test instance defined in workspace rules. Never modify production.
- Load test credentials from ignored `.env`; commit only `.env.sample`. Never print or commit secrets.

## Commits

- Commit validated slices regularly; stage explicit files and preserve unrelated work.
- Use lowercase `type(component,phase.task): description`, for example `feat(catalog,p2.r1): reconcile native bindings`.
- Use task IDs from the plan, no spaces around commas, and an imperative description without a trailing full stop.
- Abbreviate consecutive same-phase/task-series ranges: `catalog,p1.p5-6`. List other tasks separately; prefer separate task commits.
- Never add a Codex/GPT co-author or commit secrets.
- Rewrite published history only when explicitly authorized; verify remote tips, use explicit force-with-lease, then verify pushed commits.
