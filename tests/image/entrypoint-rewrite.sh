#!/usr/bin/env bash
# Image test for the entrypoint's system.xml rewrite (REVIEW-2026-09-24 S5-R1), in disposable containers.
#
#   tests/image/entrypoint-rewrite.sh [image]      (default capische/jellyfinmod:0.1.0.0; needs Docker)
#
# Runs this checkout's docker/jellyfinmod-entrypoint.sh inside the image against a scratch config volume, with the
# server binary replaced by a stub that exits at once, so nothing but the entrypoint touches system.xml:
#   1. a normal volume: the entry is added, the rest of the file is kept, the backup exists for one start;
#   2. a volume that fills up during the rewrite (tmpfs with too little room for the new file): system.xml is
#      byte-identical afterwards and the refusal is logged;
#   3. a read-only config directory: byte-identical, refusal logged;
#   4. the next start on the normal volume removes the one-start backup.
# Leaves nothing behind: every container is --rm and every scratch directory is removed.
set -euo pipefail

image="${1:-capische/jellyfinmod:0.1.0.0}"
here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
scratch="$(mktemp -d "${TMPDIR:-/tmp}/jfmod-entrypoint.XXXXXX")"
trap 'rm -rf "$scratch"' EXIT
chmod 0777 "$scratch"

fail() { echo "FAIL: $*" >&2; exit 1; }
pass() { echo "ok - $*"; }

# A system.xml shaped like Jellyfin's own (XmlSerializer, two-space indent, no final newline), padded to ~40 KB so
# the full-volume case truncates the rewrite in the middle, after the point where the entry is inserted.
seed="$scratch/seed"
mkdir -p "$seed"
{
    printf '%s\n' '<?xml version="1.0" encoding="utf-8"?>' \
        '<ServerConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">' \
        '  <PluginRepositories>' '    <RepositoryInfo>' '      <Name>Jellyfin Stable</Name>' \
        '      <Url>https://repo.jellyfin.org/files/plugin/manifest.json</Url>' '      <Enabled>true</Enabled>' \
        '    </RepositoryInfo>' '  </PluginRepositories>'
    for i in $(seq 1 700); do printf '  <JellyfinModPadding%03d>%s</JellyfinModPadding%03d>\n' "$i" "operator-setting-$i" "$i"; done
    printf '%s' '</ServerConfiguration>'
} >"$seed/system.xml"
printf '%s\n' '<?xml version="1.0" encoding="utf-8"?>' '<NetworkConfiguration>' '  <InternalHttpPort>8096</InternalHttpPort>' \
    '  <BaseUrl />' '</NetworkConfiguration>' >"$seed/network.xml"
printf '#!/bin/sh\necho "[stub] server would start here"\n' >"$seed/jellyfin"
chmod 0755 "$seed/jellyfin"
chmod -R a+rX "$seed"
original_sum="$(sha256sum "$seed/system.xml" | cut -d' ' -f1)"

run() { # run <config mount options...> -- <shell prelude>
    local mounts=() prelude
    while [[ $1 != -- ]]; do mounts+=("$1"); shift; done
    shift
    prelude="$1"
    docker run --rm --user 1000:1000 --network none \
        -e JELLYFIN_DATA_DIR=/config -e JELLYFIN_CONFIG_DIR=/config/config -e JELLYFIN_LOG_DIR=/config/log \
        -v "$here/docker/jellyfinmod-entrypoint.sh:/test/entrypoint.sh:ro" -v "$seed:/seed:ro" \
        -v "$seed/jellyfin:/jellyfin/jellyfin:ro" "${mounts[@]}" \
        --entrypoint /bin/sh "$image" -c "$prelude; sh /test/entrypoint.sh; echo SUM \$(sha256sum /config/config/system.xml | cut -d' ' -f1); ls -a /config/config" 2>&1
}

# 1. Normal volume.
normal="$scratch/normal"
mkdir -p "$normal/config" && cp "$seed/system.xml" "$seed/network.xml" "$normal/config/" && chmod -R 0777 "$normal"
out="$(run -v "$normal:/config" -- 'true')"
grep -q 'registered the repository' <<<"$out" || fail "normal rewrite did not register: $out"
grep -q '/JellyfinMod/Repository<' "$normal/config/system.xml" || fail "entry missing"
[[ $(grep -c . "$normal/config/system.xml") -eq $(($(grep -c . "$seed/system.xml") + 5)) ]] || fail "unexpected line count after the rewrite"
grep -q 'JellyfinModPadding700' "$normal/config/system.xml" && tail -c 30 "$normal/config/system.xml" | grep -q '</ServerConfiguration>' ||
    fail "the rewrite lost the end of the file"
[[ -f $normal/config/system.xml.jellyfinmod-bak ]] && cmp -s "$normal/config/system.xml.jellyfinmod-bak" "$seed/system.xml" ||
    fail "no byte-identical one-start backup"
pass "normal volume: entry added, every other line kept, one-start backup identical to the original"

# 2. The volume fills during the rewrite: a small tmpfs is filled until only about half of system.xml's size is
#    free (whatever the kernel's page size), so the new copy is cut off after the inserted entry.
mkdir -p "$scratch/fullroot" && chmod 0777 "$scratch/fullroot"
out="$(run -v "$scratch/fullroot:/config" --mount type=tmpfs,destination=/config/config,tmpfs-size=262144,tmpfs-mode=0777 -- \
    'cp /seed/system.xml /seed/network.xml /config/config/ &&
     free=$(df -k /config/config | awk "NR == 2 { print \$4 }") && half=$(( $(wc -c </seed/system.xml) / 2048 )) &&
     dd if=/dev/zero of=/config/config/.fill bs=1024 count=$((free - half)) 2>/dev/null;
     echo FREE $(df -k /config/config | awk "NR == 2 { print \$4 }")')"
sum="$(sed -n 's/^SUM //p' <<<"$out")"
[[ $sum == "$original_sum" ]] || fail "full volume changed system.xml ($sum): $out"
grep -q 'could not rewrite /config/config/system.xml completely' <<<"$out" || fail "full volume refusal not logged: $out"
grep -q 'system.xml.jellyfinmod' <<<"$(sed -n '/^SUM/,$p' <<<"$out")" && fail "full volume left a temporary file"
pass "volume full during the rewrite ($(sed -n 's/^FREE //p' <<<"$out") KiB free for a $(($(wc -c <"$seed/system.xml") / 1024)) KiB file): system.xml byte-identical ($original_sum), refusal logged, no temporary left"

# 3. Read-only config directory.
readonly_dir="$scratch/readonly"
mkdir -p "$readonly_dir" "$scratch/roroot" && chmod 0777 "$scratch/roroot" && cp "$seed/system.xml" "$seed/network.xml" "$readonly_dir/"
chmod -R a+rX "$readonly_dir"
out="$(run -v "$scratch/roroot:/config" -v "$readonly_dir:/config/config:ro" -- 'true')"
cmp -s "$readonly_dir/system.xml" "$seed/system.xml" || fail "read-only directory changed system.xml"
grep -q 'could not rewrite /config/config/system.xml completely' <<<"$out" || fail "read-only refusal not logged: $out"
pass "read-only config directory: system.xml byte-identical, refusal logged"

# 4. The next start removes the backup and leaves the registered file alone.
before="$(sha256sum "$normal/config/system.xml" | cut -d' ' -f1)"
run -v "$normal:/config" -- 'true' >/dev/null
[[ ! -e $normal/config/system.xml.jellyfinmod-bak ]] || fail "the backup outlived its one start"
[[ "$(sha256sum "$normal/config/system.xml" | cut -d' ' -f1)" == "$before" ]] || fail "second start changed system.xml"
pass "next start: backup removed, registered system.xml unchanged"
echo "PASS: entrypoint system.xml rewrite (normal, full volume, read-only, one-start backup)"
