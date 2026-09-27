#!/usr/bin/env bash
# Image test for the entrypoint's plugin install rule (PHASE7 §4.8, S5), in disposable containers.
#
#   tests/image/entrypoint-install.sh [image]      (default capische/jellyfin-mod:0.1.0.0; needs Docker)
#
# Runs this checkout's docker/jellyfinmod-entrypoint.sh inside the image against scratch config volumes, with the
# server binary replaced by a stub that exits at once and a stand-in bundled plugin (version 0.1.0.0, built
# 2026-09-27T05:11:47Z), so only the install step acts. The repository is marked as registered, so that step is idle.
#   1. empty volume: installed;
#   2. the same version from an earlier build, Disabled: replaced by the later build, still Disabled;
#   3. the same build: kept;
#   4. the same version from a later build: kept;
#   5. the same version without a build timestamp (a hand-deployed build): kept;
#   6. a newer version: kept;
#   7. an older version: the bundled version is installed beside it;
#   8. the same version from a later build written with an offset (04:30-02:00 is 06:30Z): kept;
#   9. the same version with an unreadable timestamp ("0000"): kept;
#  10. a later build in JellyfinMod_0.1.0.0 (Disabled) beside an earlier one in another folder: both kept as they are;
#  11. an earlier build in JellyfinMod_0.1.0.0 (Disabled) beside an earlier one in another folder (Active): only
#      JellyfinMod_0.1.0.0 is replaced, and it stays Disabled;
#  12. the same version only in another folder: kept, nothing installed.
# Leaves nothing behind: every container is --rm and the scratch directory is removed.
set -euo pipefail

image="${1:-capische/jellyfin-mod:0.1.0.0}"
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
scratch="$(mktemp -d "${TMPDIR:-/tmp}/jfmod-install.XXXXXX")"
trap 'rm -rf "$scratch"' EXIT
chmod 0777 "$scratch"
guid="6f1a2b3c-4d5e-4f60-9a71-8b2c3d4e5f60"

fail() { echo "FAIL: $*" >&2; exit 1; }
pass() { echo "ok - $*"; }

meta() { # meta <version> <timestamp or empty> [status]
    printf '{\n  "guid": "%s",\n  "name": "JellyfinMod",\n  "version": "%s",\n' "$guid" "$1"
    [[ -n $2 ]] && printf '  "timestamp": "%s",\n' "$2"
    [[ -n ${3:-} ]] && printf '  "status": "%s",\n' "$3"
    printf '  "assemblies": ["JellyfinMod.dll"]\n}\n'
}

bundled="$scratch/bundled"
mkdir -p "$bundled"
meta 0.1.0.0 2026-09-27T05:11:47Z >"$bundled/meta.json"
echo bundled >"$bundled/JellyfinMod.dll"
printf '#!/bin/sh\nexit 0\n' >"$scratch/jellyfin"
chmod 0755 "$scratch/jellyfin"
chmod -R a+rX "$bundled"

case_dir() { # case_dir <name> [<folder> <version> <timestamp> [status]]: a volume with one installed plugin folder
    local dir="$scratch/$1"
    mkdir -p "$dir/config" "$dir/plugins" "$dir/jellyfinmod-image"
    : >"$dir/jellyfinmod-image/repository-registered"
    if [[ $# -gt 1 ]]; then
        mkdir -p "$dir/plugins/$2"
        meta "$3" "$4" "${5:-}" >"$dir/plugins/$2/meta.json"
        echo installed >"$dir/plugins/$2/JellyfinMod.dll"
    fi
    chmod -R 0777 "$dir"
    echo "$dir"
}

run() { # run <volume>: one entrypoint start, printing its log
    docker run --rm --user 1000:1000 --network none \
        -e JELLYFIN_DATA_DIR=/config -e JELLYFIN_CONFIG_DIR=/config/config -e JELLYFIN_LOG_DIR=/config/log \
        -v "$here/docker/jellyfinmod-entrypoint.sh:/test/entrypoint.sh:ro" -v "$bundled:/opt/jellyfinmod/plugin:ro" \
        -v "$scratch/jellyfin:/jellyfin/jellyfin:ro" -v "$1:/config" \
        --entrypoint /bin/sh "$image" -c "sh /test/entrypoint.sh" 2>&1
}

dll() { cat "$1/plugins/$2/JellyfinMod.dll"; }

add_folder() { # add_folder <volume> <folder> <version> <timestamp> [status]: a second installed folder
    mkdir -p "$1/plugins/$2"
    meta "$3" "$4" "${5:-}" >"$1/plugins/$2/meta.json"
    echo installed >"$1/plugins/$2/JellyfinMod.dll"
    chmod -R 0777 "$1/plugins/$2"
}

# 1. Empty volume.
v="$(case_dir empty)"
out="$(run "$v")"
[[ $(dll "$v" JellyfinMod_0.1.0.0) == bundled ]] && grep -q 'installed JellyfinMod 0.1.0.0' <<<"$out" || fail "empty volume: $out"
pass "empty volume: 0.1.0.0 installed"

# 2. Same version, earlier build, Disabled (Jellyfin's own rewritten timestamp format).
v="$(case_dir earlier JellyfinMod_0.1.0.0 0.1.0.0 2026-09-25T06:09:17.0000000Z Disabled)"
out="$(run "$v")"
[[ $(dll "$v" JellyfinMod_0.1.0.0) == bundled ]] || fail "earlier build not replaced: $out"
grep -q '"status": "Disabled"' "$v/plugins/JellyfinMod_0.1.0.0/meta.json" || fail "Disabled status lost on the rebuild"
grep -q 'replaced JellyfinMod 0.1.0.0 build 2026-09-25T06:09:17Z with build 2026-09-27T05:11:47Z' <<<"$out" || fail "rebuild not logged: $out"
[[ $(find "$v/plugins" -maxdepth 1 -name 'JellyfinMod_*' | wc -l) -eq 1 ]] || fail "rebuild left a second folder"
pass "same version, earlier build: replaced in place, Disabled kept, logged"

# 3. The same build.
v="$(case_dir same JellyfinMod_0.1.0.0 0.1.0.0 2026-09-27T05:11:47.0000000Z)"
out="$(run "$v")"
[[ $(dll "$v" JellyfinMod_0.1.0.0) == installed ]] && grep -q 'does not replace it' <<<"$out" || fail "same build replaced: $out"
pass "same build: kept"

# 4. Same version, later build.
v="$(case_dir later JellyfinMod_0.1.0.0 0.1.0.0 2026-09-28T00:00:00Z)"
out="$(run "$v")"
[[ $(dll "$v" JellyfinMod_0.1.0.0) == installed ]] || fail "later build replaced by an earlier one: $out"
pass "same version, later build: kept"

# 5. Same version without a timestamp.
v="$(case_dir untimed JellyfinMod_0.1.0.0 0.1.0.0 '')"
out="$(run "$v")"
[[ $(dll "$v" JellyfinMod_0.1.0.0) == installed ]] || fail "untimed build replaced: $out"
pass "same version without a build timestamp: kept"

# 6. A newer version.
v="$(case_dir newer JellyfinMod_0.1.0.1 0.1.0.1 2026-09-26T00:00:00Z)"
out="$(run "$v")"
[[ $(dll "$v" JellyfinMod_0.1.0.1) == installed && ! -e $v/plugins/JellyfinMod_0.1.0.0 ]] || fail "newer version touched: $out"
pass "newer version: kept, nothing installed beside it"

# 7. An older version.
v="$(case_dir older JellyfinMod_0.0.9.0 0.0.9.0 2026-09-01T00:00:00Z)"
out="$(run "$v")"
[[ $(dll "$v" JellyfinMod_0.1.0.0) == bundled && $(dll "$v" JellyfinMod_0.0.9.0) == installed ]] &&
    grep -q 'upgraded JellyfinMod 0.0.9.0 to 0.1.0.0' <<<"$out" || fail "older version not upgraded: $out"
pass "older version: 0.1.0.0 installed beside it"
# 8. Same version, a later build written with an offset.
v="$(case_dir offset JellyfinMod_0.1.0.0 0.1.0.0 2026-09-27T04:30:00-02:00)"
out="$(run "$v")"
[[ $(dll "$v" JellyfinMod_0.1.0.0) == installed ]] && grep -q '(2026-09-27T06:30:00Z)' <<<"$out" ||
    fail "a later build with an offset was replaced: $out"
pass "same version, later build with an offset (06:30Z): kept"

# 9. Same version, unreadable timestamp.
v="$(case_dir unreadable JellyfinMod_0.1.0.0 0.1.0.0 0000)"
out="$(run "$v")"
[[ $(dll "$v" JellyfinMod_0.1.0.0) == installed ]] && grep -q 'no readable build timestamp' <<<"$out" ||
    fail "an unreadable timestamp authorised a replacement: $out"
pass "same version, unreadable timestamp: kept"

# 10. A later canonical build (Disabled) beside an earlier build in another folder.
v="$(case_dir mixed-later JellyfinMod_0.1.0.0 0.1.0.0 2026-09-28T00:00:00Z Disabled)"
add_folder "$v" JellyfinMod 0.1.0.0 2026-09-01T00:00:00Z Active
out="$(run "$v")"
[[ $(dll "$v" JellyfinMod_0.1.0.0) == installed && $(dll "$v" JellyfinMod) == installed ]] &&
    grep -q '"status": "Disabled"' "$v/plugins/JellyfinMod_0.1.0.0/meta.json" || fail "a later canonical build was touched: $out"
pass "later build in JellyfinMod_0.1.0.0 beside an earlier folder: both kept, still Disabled"

# 11. An earlier canonical build (Disabled) beside an earlier build in another folder (Active).
v="$(case_dir mixed-earlier JellyfinMod_0.1.0.0 0.1.0.0 2026-09-25T06:09:17.0000000Z Disabled)"
add_folder "$v" JellyfinMod 0.1.0.0 2026-09-01T00:00:00Z Active
out="$(run "$v")"
[[ $(dll "$v" JellyfinMod_0.1.0.0) == bundled && $(dll "$v" JellyfinMod) == installed ]] &&
    grep -q '"status": "Disabled"' "$v/plugins/JellyfinMod_0.1.0.0/meta.json" || fail "the canonical folder was not replaced as its own: $out"
pass "earlier builds in two folders: only JellyfinMod_0.1.0.0 replaced, its Disabled status kept"

# 12. The same version only in another folder.
v="$(case_dir elsewhere JellyfinMod 0.1.0.0 2026-09-01T00:00:00Z)"
out="$(run "$v")"
[[ ! -e $v/plugins/JellyfinMod_0.1.0.0 && $(dll "$v" JellyfinMod) == installed ]] && grep -q 'not installed in JellyfinMod_0.1.0.0' <<<"$out" ||
    fail "a version held elsewhere was installed again: $out"
pass "same version only in another folder: kept, nothing installed"
echo "PASS: entrypoint plugin install rule (12 cases)"
