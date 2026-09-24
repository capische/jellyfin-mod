#!/bin/sh
# JellyfinMod image entrypoint (PHASE7 §4.8, S5).
#
# Runs before Jellyfin, while nothing else is reading the configuration, and then hands over to the
# stock entrypoint with exec. Three jobs, each idempotent, none fatal:
#
#   1. Install the bundled plugin into the config volume when it is missing or older than the one in
#      the image. Never downgrade, never touch another plugin.
#   2. Register the plugin's own repository once (accepted decision 11), on the very first start
#      too. It points at this server's own address, so nothing external is contacted. Image configuration, not plugin behaviour: the
#      plugin never edits the repository list itself.
#   3. Honour JELLYFINMOD_UI_TAKEOVER=false at first start, before the plugin has a configuration.
#
# A failure in any of these logs and continues: Jellyfin starts either way, stock at worst.

set -u

data_dir="${JELLYFIN_DATA_DIR:-/config}"
config_dir="${JELLYFIN_CONFIG_DIR:-$data_dir/config}"
bundled=/opt/jellyfinmod/plugin
plugins_dir="$data_dir/plugins"
state_dir="$data_dir/jellyfinmod-image"
repository_marker="$state_dir/repository-registered"
repository_name="JellyfinMod (this server)"

log() {
    echo "[jellyfinmod-image] $*" >&2
}

# The value of a top-level string field in a meta.json. JPRM and Jellyfin both write one field per
# line, and the image carries no JSON tool, so a line match is enough and is all this relies on.
meta_value() {
    sed -n "s/^[[:space:]]*\"$2\"[[:space:]]*:[[:space:]]*\"\\([^\"]*\\)\".*/\\1/p" "$1" | head -n 1
}

# True when $1 sorts strictly after $2 as a version.
version_newer() {
    [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -n 1)" = "$1" ]
}

install_plugin() {
    guid="$(meta_value "$bundled/meta.json" guid)"
    version="$(meta_value "$bundled/meta.json" version)"
    if [ -z "$guid" ] || [ -z "$version" ]; then
        log "the bundled plugin has no readable meta.json; nothing installed"
        return 0
    fi

    newest=""
    newest_dir=""
    for meta in "$plugins_dir"/*/meta.json; do
        [ -f "$meta" ] || continue
        [ "$(meta_value "$meta" guid)" = "$guid" ] || continue
        found="$(meta_value "$meta" version)"
        if [ -z "$newest" ] || version_newer "$found" "$newest"; then
            newest="$found"
            newest_dir="$(dirname "$meta")"
        fi
    done

    if [ -n "$newest" ] && ! version_newer "$version" "$newest"; then
        log "JellyfinMod $newest is installed; the image carries $version and does not replace it"
        return 0
    fi

    target="$plugins_dir/JellyfinMod_$version"
    staging="$target.installing"
    mkdir -p "$plugins_dir" || return 0
    rm -rf "$staging"
    if ! cp -R "$bundled" "$staging"; then
        log "could not copy the plugin into $plugins_dir; Jellyfin starts without it"
        rm -rf "$staging"
        return 0
    fi

    # An administrator who disabled the plugin keeps it disabled across an image upgrade.
    if [ -n "$newest_dir" ]; then
        status="$(meta_value "$newest_dir/meta.json" status)"
        if [ -n "$status" ] && [ "$status" != "Active" ]; then
            sed -i "1a\\    \"status\": \"$status\"," "$staging/meta.json"
        fi
    fi

    rm -rf "$target"
    mv "$staging" "$target"
    if [ -n "$newest" ]; then
        log "upgraded JellyfinMod $newest to $version; Jellyfin keeps the newest and retires the older folder"
    else
        log "installed JellyfinMod $version"
    fi
}

# Read one element's text from network.xml, or print nothing.
network_value() {
    file="$config_dir/network.xml"
    [ -f "$file" ] || return 0
    sed -n "s:.*<$1>\\([^<]*\\)</$1>.*:\\1:p" "$file" | head -n 1
}

# First start only: run Jellyfin once until its start-up completes, then stop it.
#
# A fresh server's setup migrations replace the whole repository list with the default one, so an
# entry written before the first start does not survive it. Letting that first start finish means
# the migrations have run and are recorded, and the entry added afterwards is kept. It costs one
# extra start-up, once per config volume; the wizard is untouched because it has not been completed.
first_start_bootstrap() {
    log "first start: letting Jellyfin initialise its configuration before registering the repository"
    /jellyfin/jellyfin "$@" &
    child=$!
    stopping=0
    trap 'stopping=1; kill -TERM "$child" 2>/dev/null' TERM INT
    waited=0
    while kill -0 "$child" 2>/dev/null && [ "$waited" -lt 600 ]; do
        if grep -qs "Startup complete" "${JELLYFIN_LOG_DIR:-$data_dir/log}"/log_*.log; then
            break
        fi
        sleep 2
        waited=$((waited + 2))
    done
    kill -TERM "$child" 2>/dev/null
    wait "$child" 2>/dev/null
    trap - TERM INT
    if [ "$stopping" = 1 ]; then
        log "stopped during the first start; the repository is registered on the next one"
        exit 0
    fi
    log "first start: initialisation finished after ${waited}s; starting Jellyfin"
}

register_repository() {
    # Once only. An administrator who removes the entry has decided, and a restart must not undo it.
    if [ -e "$repository_marker" ]; then
        # The copy kept by the start that registered the entry has served its one start.
        rm -f "$config_dir/system.xml.jellyfinmod-bak" 2>/dev/null
        return 0
    fi
    mkdir -p "$state_dir" || return 0

    system="$config_dir/system.xml"
    if [ ! -f "$system" ]; then
        first_start_bootstrap "$@"
        if [ ! -f "$system" ]; then
            log "Jellyfin wrote no $system during its first start; no repository registered"
            return 0
        fi
    fi

    # The server's own address, as it listens inside the container, so nothing external is contacted.
    port="$(network_value InternalHttpPort)"
    base_url="$(network_value BaseUrl)"
    url="http://localhost:${port:-8096}${base_url}/JellyfinMod/Repository"
    entry="    <RepositoryInfo>
      <Name>$repository_name</Name>
      <Url>$url</Url>
      <Enabled>true</Enabled>
    </RepositoryInfo>"

    if grep -q '/JellyfinMod/Repository<' "$system"; then
        log "a JellyfinMod repository is already registered; not adding another"
    else
        if grep -q '<PluginRepositories>' "$system"; then
            ENTRY="$entry" awk '{ print } /<PluginRepositories>/ && !done { print ENVIRON["ENTRY"]; done = 1 }' \
                "$system" >"$system.jellyfinmod"
        elif grep -q '<PluginRepositories */>' "$system"; then
            ENTRY="$entry" awk '/<PluginRepositories *\/>/ && !done {
                print "  <PluginRepositories>"; print ENVIRON["ENTRY"]; print "  </PluginRepositories>"; done = 1; next } { print }' \
                "$system" >"$system.jellyfinmod"
        else
            ENTRY="$entry" awk '/<\/ServerConfiguration>/ && !done {
                print "  <PluginRepositories>"; print ENVIRON["ENTRY"]; print "  </PluginRepositories>"; done = 1 } { print }' \
                "$system" >"$system.jellyfinmod"
        fi
        # Replace system.xml only with a complete rewrite (REVIEW-2026-09-24 S5-R1): awk must succeed, the output
        # must be exactly the input plus the entry's lines, and end with the closing element. A full or
        # read-only volume, or a failed awk, leaves the original byte for byte and says so.
        status=$?
        # Records, not newlines: Jellyfin writes system.xml without a final newline and awk adds one.
        in_lines="$(awk 'END { print NR }' "$system" 2>/dev/null || echo -1)"
        out_lines="$(awk 'END { print NR }' "$system.jellyfinmod" 2>/dev/null || echo -2)"
        entry_lines="$(printf '%s\n' "$entry" | awk 'END { print NR }')"
        if grep -q '<PluginRepositories>' "$system"; then
            added=$entry_lines
        else
            added=$((entry_lines + 2))
            # The self-closing form is replaced by the open and close lines around the entry.
            grep -q '<PluginRepositories */>' "$system" && added=$((entry_lines + 1))
        fi
        if [ "$status" -eq 0 ] && [ "$out_lines" -eq $((in_lines + added)) ] &&
            grep -q '/JellyfinMod/Repository<' "$system.jellyfinmod" &&
            tail -n 3 "$system.jellyfinmod" | grep -q '</ServerConfiguration>' &&
            cp -p "$system" "$system.jellyfinmod-bak" &&
            mv "$system.jellyfinmod" "$system"; then
            log "registered the repository \"$repository_name\" at $url (previous system.xml kept as system.xml.jellyfinmod-bak for one start)"
        else
            rm -f "$system.jellyfinmod" "$system.jellyfinmod-bak" 2>/dev/null
            log "could not rewrite $system completely (awk status $status, $out_lines of $((in_lines + added)) lines); left it unchanged and registered nothing"
            return 0
        fi
    fi

    : >"$repository_marker"
}

first_start_takeover() {
    configuration="$plugins_dir/configurations/JellyfinMod.xml"
    [ -e "$configuration" ] && return 0
    case "$(printf '%s' "${JELLYFINMOD_UI_TAKEOVER:-}" | tr '[:upper:]' '[:lower:]')" in
        "" | true | 1 | yes | on) return 0 ;;
        false | 0 | no | off) ;;
        *)
            log "JELLYFINMOD_UI_TAKEOVER=${JELLYFINMOD_UI_TAKEOVER} is not true or false; ignored"
            return 0
            ;;
    esac
    mkdir -p "$(dirname "$configuration")" || return 0
    printf '%s\n' \
        '<?xml version="1.0" encoding="utf-8"?>' \
        '<PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">' \
        '  <UiTakeoverEnabled>false</UiTakeoverEnabled>' \
        '</PluginConfiguration>' >"$configuration" &&
        log "JELLYFINMOD_UI_TAKEOVER=false: the plugin starts with its interface takeover off"
}

install_plugin
first_start_takeover
register_repository "$@"

exec /jellyfin/jellyfin "$@"
