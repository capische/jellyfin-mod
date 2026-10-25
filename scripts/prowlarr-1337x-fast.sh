#!/usr/bin/env bash
# Make Prowlarr's 1337x keyword searches read one results page instead of four.
#
# Prowlarr's official 1337x definition reads pages 1-4 for every keyword search, three seconds
# apart, so one search takes 10-12 s. This script writes Definitions/Custom/1337x-fast.yml, a copy
# of your current official 1337x.yml whose keyword pages 2-4 point at page 1 (Prowlarr skips the
# repeated request), and switches your 1337x indexers to it through Prowlarr's API. The indexer
# keeps its id, name, tags and settings. RSS (searches without keywords) is unchanged.
#
# Usage: prowlarr-1337x-fast.sh [--config DIR] [--url URL] [--restore] [--no-test]
#   --config DIR  Prowlarr's config folder, the one holding Definitions/ and config.xml
#                 (default: $PROWLARR_CONFIG, else /config; linuxserver/prowlarr keeps it at /config)
#   --url URL     Prowlarr's address (default: $PROWLARR_URL, else http://localhost:9696)
#   --restore     switch the indexers back to the official 1337x and remove the custom file
#   --no-test     skip the timed test search
# API key: $PROWLARR_API_KEY, else the file named by $PROWLARR_API_KEY_FILE, else <config>/config.xml.
# Needs bash, curl, jq, sed and grep. Safe to run again; run it again after Prowlarr updates its
# definitions so 1337x-fast follows the new official 1337x.yml.

set +x # never trace this script: a trace would print the API key
set -euo pipefail

config="${PROWLARR_CONFIG:-/config}"
url="${PROWLARR_URL:-http://localhost:9696}"
restore=0
test_search=1
query="the%20matrix" # URL-encoded; a title with many results on 1337x

die() { printf 'error: %s\n' "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --config) [ $# -ge 2 ] || die "--config needs a folder"; config="$2"; shift 2 ;;
    --url) [ $# -ge 2 ] || die "--url needs an address"; url="$2"; shift 2 ;;
    --restore) restore=1; shift ;;
    --no-test) test_search=0; shift ;;
    -h|--help) sed -n '2,18p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) die "unknown option: $1 (see --help)" ;;
  esac
done
url="${url%/}"

for tool in curl jq sed grep; do
  command -v "$tool" >/dev/null 2>&1 || die "$tool is not installed"
done

official="$config/Definitions/1337x.yml"
custom="$config/Definitions/Custom/1337x-fast.yml"

# --- API key (never printed, never on a command line) ---
if [ -n "${PROWLARR_API_KEY:-}" ]; then
  api_key="$PROWLARR_API_KEY"
elif [ -n "${PROWLARR_API_KEY_FILE:-}" ]; then
  [ -r "$PROWLARR_API_KEY_FILE" ] || die "cannot read PROWLARR_API_KEY_FILE"
  api_key="$(tr -d ' \r\n' < "$PROWLARR_API_KEY_FILE")"
elif [ -r "$config/config.xml" ]; then
  api_key="$(sed -n 's:.*<ApiKey>\(.*\)</ApiKey>.*:\1:p' "$config/config.xml" | head -n 1)"
else
  die "no API key: set PROWLARR_API_KEY or PROWLARR_API_KEY_FILE, or pass --config with config.xml in it"
fi
[ -n "$api_key" ] || die "the API key is empty"

tmp="$(mktemp -d)"
custom_new="" # the temporary sibling this run created, if any
trap 'rm -rf "$tmp"; [ -z "$custom_new" ] || rm -f "$custom_new"' EXIT

# api METHOD PATH [BODY_FILE]: prints the response body; fails on an HTTP error.
# The key goes to curl through its stdin config, so it never shows in the process list; -q (it
# must come first) ignores the user's .curlrc, which could turn on verbose output with the header.
api() {
  local method="$1" path="$2" body="${3:-}" result code
  local args=(-q -sS -o "$tmp/out" -w '%{http_code} %{time_total}' -X "$method" -K -)
  [ -n "$body" ] && args+=(-H 'Content-Type: application/json' --data-binary "@$body")
  result="$(printf 'header = "X-Api-Key: %s"\n' "$api_key" | curl "${args[@]}" "$url$path")" \
    || die "cannot reach Prowlarr at $url"
  code="${result%% *}"
  printf '%s' "${result#* }" > "$tmp/time"
  case "$code" in
    2??) cat "$tmp/out" ;;
    *) printf 'error: %s %s answered HTTP %s:\n' "$method" "$path" "$code" >&2
       head -c 600 "$tmp/out" >&2; printf '\n' >&2; exit 1 ;;
  esac
}

# set_definition ID NAME: points indexer ID at definition NAME, keeping everything else.
# Prowlarr takes the definition from the hidden "definitionFile" setting, not from definitionName.
set_definition() {
  api GET "/api/v1/indexer/$1" \
    | jq --arg d "$2" '.definitionName = $d | (.fields[] | select(.name == "definitionFile") | .value) = $d' \
    > "$tmp/indexer.json"
  # forceSave: the definition change does not change whether 1337x is reachable, so Prowlarr's
  # connection test is skipped; the timed search below shows whether it answers.
  api PUT "/api/v1/indexer/$1?forceSave=true" "$tmp/indexer.json" > /dev/null
  [ "$(api GET "/api/v1/indexer/$1" | jq -r '.definitionName')" = "$2" ] \
    || die "Prowlarr did not switch indexer $1 to $2"
}

# timed_search ID: one keyword search through Prowlarr, printing seconds and result rows.
timed_search() {
  local rows
  # In a subshell, so a failed test search is reported without stopping the script.
  ( api GET "/api/v1/search?query=$query&indexerIds=$1&type=search&limit=100" ) > "$tmp/search.json" \
    || { echo "the test search failed"; return 0; }
  rows="$(jq 'length' "$tmp/search.json")"
  printf '%s rows in %s s\n' "$rows" "$(cut -c1-4 "$tmp/time")"
}

indexers_on() { # definitionName -> "id<TAB>name" lines
  api GET /api/v1/indexer | jq -r --arg d "$1" '.[] | select(.definitionName == $d) | "\(.id)\t\(.name)"'
}

api GET /api/v1/system/status > /dev/null

# --- restore ---
if [ "$restore" = 1 ]; then
  [ -r "$official" ] || die "$official is missing; the official definition must be there to switch back"
  found=0
  list="$(indexers_on 1337x-fast)"
  while IFS="$(printf '\t')" read -r id name; do
    [ -n "$id" ] || continue
    found=1
    set_definition "$id" 1337x
    printf 'indexer %s "%s": switched back to 1337x\n' "$id" "$name"
  done <<< "$list"
  [ "$found" = 1 ] || echo "no indexer uses 1337x-fast"
  if [ -e "$custom" ]; then rm -f "$custom"; echo "removed $custom"; fi
  echo "done."
  exit 0
fi

# --- write the custom definition from the official one ---
[ -r "$official" ] || die "$official not found: is --config Prowlarr's config folder (the one holding Definitions/)?"
page_pattern='{{ if \.Keywords }}[234]{{ else }}1{{ end }}'
pages="$(grep -c "$page_pattern" "$official" || true)"
grep -q '^id: 1337x$' "$official" && grep -q '^name: 1337x$' "$official" && [ "$pages" = 3 ] || die \
  "$official no longer looks as expected (id/name lines, or three keyword page paths 2-4: found $pages).
The official definition has changed; the one-page edit has to be redone by hand (see the how-to)."

{
  echo "# Generated by prowlarr-1337x-fast.sh from the official 1337x.yml: keyword searches read page 1 only."
  sed -e 's/^id: 1337x$/id: 1337x-fast/' \
      -e 's/^name: 1337x$/name: 1337x Fast/' \
      -e "s/$page_pattern/1/" "$official"
} > "$tmp/1337x-fast.yml"
[ "$(grep -c "$page_pattern" "$tmp/1337x-fast.yml" || true)" = 0 ] || die "the page edit did not apply"

mkdir -p "$(dirname "$custom")"
if [ -e "$custom" ] && cmp -s "$tmp/1337x-fast.yml" "$custom"; then
  echo "$custom is up to date"
else
  replacing=0
  [ -e "$custom" ] && replacing=1
  # Write a sibling and rename it over the file, so Prowlarr never sees a half-written definition.
  # mktemp creates it exclusively, under a name that does not end in .yml.
  custom_new="$(mktemp "$(dirname "$custom")/.1337x-fast.XXXXXX")"
  cat "$tmp/1337x-fast.yml" > "$custom_new"
  chmod 0644 "$custom_new"
  mv -f "$custom_new" "$custom"
  custom_new=""
  echo "wrote $custom"
  if [ "$replacing" = 1 ]; then
    echo "note: Prowlarr keeps a loaded definition in memory; restart Prowlarr so it uses the new file."
  fi
fi

# A running Prowlarr picks up a new file in Definitions/Custom without a restart (it keeps a
# definition's content cached once loaded, hence the restart note above).
if ! api GET /api/v1/indexer/schema | jq -e 'any(.[]; .definitionName == "1337x-fast")' > /dev/null; then
  die "Prowlarr does not offer 1337x-fast yet. Restart Prowlarr, then run this script again."
fi

# --- switch the indexers ---
switched=""
list="$(indexers_on 1337x)"
while IFS="$(printf '\t')" read -r id name; do
  [ -n "$id" ] || continue
  if [ "$test_search" = 1 ]; then printf 'indexer %s "%s" before: ' "$id" "$name"; timed_search "$id"; fi
  set_definition "$id" 1337x-fast
  printf 'indexer %s "%s": switched to 1337x-fast\n' "$id" "$name"
  switched="$switched $id"
done <<< "$list"

current="$(indexers_on 1337x-fast)"
[ -n "$current" ] || die "no indexer uses 1337x or 1337x-fast; add 1337x in Prowlarr first"
if [ -z "$switched" ]; then echo "every 1337x indexer already uses 1337x-fast"; fi
if [ "$test_search" = 1 ]; then
  while IFS="$(printf '\t')" read -r id name; do
    printf 'indexer %s "%s" after: ' "$id" "$name"; timed_search "$id"
  done <<< "$current"
fi
printf 'done. Undo with: %q --config %q --url %q --restore\n' "$0" "$config" "$url"
