# Faster 1337x searches in Prowlarr

## Why

Prowlarr's official 1337x definition reads four results pages for every keyword search, three
seconds apart (`requestDelay: 3`). One search therefore takes 10 to 12 seconds, more through
FlareSolverr, and JellyfinMod's release picker waits for it. Asking for fewer results does not help:
the page count is fixed in the definition.

The fix is a copy of the definition, **1337x Fast**, whose keyword pages 2 to 4 point at page 1.
Prowlarr skips a repeated request, so a search reads one page: the first 20 results in the order
the indexer asks 1337x for, in about 1 to 3 seconds. Searches without keywords (the RSS feed Radarr
and Sonarr poll) still read all four category listings, unchanged.

**What it costs:**

- A search returns up to 20 results instead of up to 80: the first page under the indexer's sort.
  The official definition sorts by time added, newest first, so by default a search returns the
  20 newest matching releases, and an older, well-seeded one beyond them is not returned. For a
  title with few releases nothing is lost; for a popular one, older releases can be.
- Optional: to get the 20 best-seeded releases instead, edit the indexer in Prowlarr, leave
  **Disable sorting** unchecked (when it is checked, 1337x is asked for no sort at all), and set
  **Sort requested from site** to `seeders` and **Order requested from site** to `desc`. 1337x can
  refuse sorted searches when its servers are busy, which shows as zero results; that is what the
  Disable sorting box is for. The script does not change these settings.
- Prowlarr's automatic definition updates no longer reach 1337x. When 1337x changes its site, or
  after Prowlarr updates its definitions, run the script again: it rebuilds 1337x Fast from the
  current official file.

## With the script

[`scripts/prowlarr-1337x-fast.sh`](../scripts/prowlarr-1337x-fast.sh) does it in one step. Run it on
the Prowlarr host, or anywhere Prowlarr's config folder is mounted. It needs `bash`, `curl` and
`jq`.

```sh
./prowlarr-1337x-fast.sh --config /path/to/prowlarr/config --url http://localhost:9696
```

- `--config` is Prowlarr's config folder, the one that holds `Definitions/` and `config.xml`. In the
  linuxserver/prowlarr image that is `/config` inside the container; on the host it is whatever
  folder you mounted there.
- The API key is read from `config.xml` in that folder. To give it yourself, set
  `PROWLARR_API_KEY`, or put it in a file and set `PROWLARR_API_KEY_FILE` to its path. The key is
  never printed and never passed on a command line.
- `--no-test` skips the timed test search.

What it does:

1. Writes `Definitions/Custom/1337x-fast.yml` from your current `Definitions/1337x.yml`: id
   `1337x-fast`, name `1337x Fast`, keyword pages 2 to 4 replaced with page 1. If the official file
   does not look as expected (1337x changed its definition), it stops and changes nothing.
2. Switches every indexer that uses the official 1337x definition to 1337x Fast, through Prowlarr's
   API. The indexer keeps its id, name, tags (FlareSolverr), priority and settings, so Radarr,
   Sonarr and JellyfinMod see the same indexer and need no change.
3. Runs one test search before and after, and prints the time and the number of results, for
   example:

   ```text
   indexer 1 "1337x" before: 80 rows in 9.22 s
   indexer 1 "1337x": switched to 1337x-fast
   indexer 1 "1337x" after: 20 rows in 2.88 s
   ```

No restart is needed the first time: Prowlarr picks up a new file in `Definitions/Custom` while it
runs. Running the script again is safe. If it replaces an existing 1337x Fast file (the official
definition had changed), it says so right away: then restart Prowlarr, which keeps a loaded
definition in memory until it restarts.

The first search after a Prowlarr restart can take 10 seconds or more on its own, while
FlareSolverr solves Cloudflare's check again.

## By hand, in Prowlarr's interface

1. In Prowlarr's config folder, copy `Definitions/1337x.yml` to `Definitions/Custom/1337x-fast.yml`
   (create `Custom` if it is not there).
2. Edit the copy:
   - `id: 1337x` → `id: 1337x-fast`
   - `name: 1337x` → `name: 1337x Fast`
   - in the second, third and fourth `search.paths` lines, replace
     `{{ if .Keywords }}2{{ else }}1{{ end }}`, `{{ if .Keywords }}3{{ else }}1{{ end }}` and
     `{{ if .Keywords }}4{{ else }}1{{ end }}` with `1`.
3. In Prowlarr, Indexers → Add Indexer, search for **1337x Fast** and add it with the settings of
   your old 1337x indexer: the same tags (the FlareSolverr tag), base URL, sort and priority.
   Test, then Save.
4. Delete the old 1337x indexer.

Done this way the indexer is a new one with a new id. Prowlarr's app sync replaces it in Radarr
and Sonarr. In JellyfinMod, press Sync Now in the Prowlarr settings: 1337x Fast appears as a new
indexer and the old one is marked as removed from Prowlarr.

## Undo

```sh
./prowlarr-1337x-fast.sh --config /path/to/prowlarr/config --url http://localhost:9696 --restore
```

It switches every 1337x Fast indexer back to the official 1337x definition and deletes
`Definitions/Custom/1337x-fast.yml`. No restart is needed.

By hand: add the official 1337x again with the same settings, delete 1337x Fast, then delete
`Definitions/Custom/1337x-fast.yml`.
