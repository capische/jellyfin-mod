#!/usr/bin/env bash
# Build a JellyfinMod release from a built jellyfin-web fork: the plugin package, the release
# archive and the Docker image context (PHASE7 §4.8, S5).
#
#   scripts/build-release.sh --web-dist <jellyfin-web>/dist [--out DIR] [--no-build]
#
# Writes, under DIR (default: artifacts/release in this checkout):
#   package/                     JellyfinMod.dll, logo.png, meta.json, jellyfinmod-web.zip
#   jellyfinmod-<version>.zip    the release archive: plugin/ (the package) and web/ (the fork's dist)
#   image/                       the Docker build context: Dockerfile, entrypoint, package/
#
# Nothing here needs Docker. Build the image wherever it will run, from image/:
#   docker build -t <name>:<tag> DIR/image
set -euo pipefail

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/artifacts/release"
web_dist=""
build=1
dotnet="${JELLYFIN_DOTNET:-dotnet}"
python="${JELLYFIN_PLUGIN_PYTHON:-python3}"

die() {
    echo "build-release: $*" >&2
    exit 2
}

while (($#)); do
    case "$1" in
        --web-dist)
            [[ $# -ge 2 ]] || die "--web-dist needs a value"
            web_dist="$2"
            shift
            ;;
        --out)
            [[ $# -ge 2 ]] || die "--out needs a value"
            out="$2"
            shift
            ;;
        --no-build) build=0 ;;
        -h | --help)
            sed -n '2,15p' "$0"
            exit 0
            ;;
        *) die "unknown option: $1" ;;
    esac
    shift
done

[[ -n $web_dist ]] || die "--web-dist is required: a built jellyfin-web dist/ (npm run build:production)"
web_dist="$(cd -- "$web_dist" && pwd)"
# Both entries and the bundle identity must be there; a stock-only build would install an interface
# the plugin refuses to serve.
for required in index.html jellyfinmod.html jellyfinmod-web.json; do
    [[ -s $web_dist/$required ]] || die "$web_dist/$required is missing; build the fork's jellyfin-mod branch first"
done

binary_dir="$root/JellyfinMod/bin/Release/net9.0"
if ((build)); then
    "$dotnet" build -c Release "$root/JellyfinMod/JellyfinMod.csproj"
fi

rm -rf "$out"
mkdir -p "$out"

# The bundle the plugin serves is dist/ without source maps (§4.5). Zipped from inside dist/ so the
# archive's paths are the bundle's own.
(cd -- "$web_dist" && zip -q -r -X "$out/jellyfinmod-web.zip" . -x '*.map')
cp "$out/jellyfinmod-web.zip" "$binary_dir/jellyfinmod-web.zip"

"$python" "$root/scripts/package_plugin.py" "$binary_dir" "$out/package"
rm -f "$out/jellyfinmod-web.zip"

version="$(sed -n 's/^version: *"\(.*\)"/\1/p' "$root/build.yaml")"
[[ -n $version ]] || die "no version in build.yaml"

# Release archive: the same package, plus the fork's dist/ for hosts that serve it themselves.
staging="$out/jellyfinmod-$version"
mkdir -p "$staging/plugin" "$staging/web"
cp "$out/package/"* "$staging/plugin/"
rsync -a --exclude '*.map' "$web_dist/" "$staging/web/"
(cd -- "$out" && zip -q -r -X "jellyfinmod-$version.zip" "jellyfinmod-$version")
rm -rf "$staging"

# Docker build context.
mkdir -p "$out/image"
cp "$root/docker/Dockerfile" "$root/docker/jellyfinmod-entrypoint.sh" "$out/image/"
cp -R "$out/package" "$out/image/package"

bundle_id="$(sed -n 's/.*"bundleId": *"\([^"]*\)".*/\1/p' "$web_dist/jellyfinmod-web.json" | head -n 1)"
echo "JellyfinMod $version, web bundle $bundle_id"
echo "  package  $out/package"
echo "  archive  $out/jellyfinmod-$version.zip"
echo "  image    $out/image  (docker build -t <name>:<tag> $out/image)"
