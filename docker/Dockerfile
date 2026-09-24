# JellyfinMod image: stock Jellyfin with the JellyfinMod plugin preinstalled (PHASE7 §4.8, S5).
#
# Nothing is compiled here. The build context is the release directory scripts/build-release.sh
# writes: this file, the entrypoint and package/ (JellyfinMod.dll, logo.png, meta.json and
# jellyfinmod-web.zip). The plugin assembly is architecture-independent, so the same context builds
# for amd64 and arm64; build it on the host that runs it, or with buildx for another platform.

# The host is pinned by digest, never by a moving tag: a host upgrade is a deliberate image release
# that runs the upstream merge routine (PHASE7 §3.4). This digest reports Jellyfin 12.0.0.
ARG JELLYFIN_IMAGE=jellyfin/jellyfin@sha256:baba630419915985442f315f08b0cf46d9f4c8a0cc4bd38e94a6d35751dd5ef5
FROM ${JELLYFIN_IMAGE}

LABEL org.opencontainers.image.title="JellyfinMod" \
      org.opencontainers.image.description="Stock Jellyfin with the JellyfinMod plugin preinstalled" \
      org.opencontainers.image.licenses="GPL-2.0-only" \
      org.opencontainers.image.source="https://github.com/capische/jellyfin-mod"

COPY package/ /opt/jellyfinmod/plugin/
COPY jellyfinmod-entrypoint.sh /usr/local/bin/jellyfinmod-entrypoint

# The web directory stays inside the container, stock on every recreate, and the plugin rewrites
# index.html in it at startup. Only the directory itself changes owner: the engine writes a
# temporary file and renames it over index.html, which needs write access to the directory, not to
# the root-owned files in it. Recursing would copy the whole web client into a new layer.
# 1000:1000 is the runtime user the documentation uses; root can write regardless. Any other user
# still starts cleanly: the plugin reports web_root_read_only and serves the interface at /web-mod.
RUN chmod 0755 /usr/local/bin/jellyfinmod-entrypoint \
    && chmod -R a+rX /opt/jellyfinmod \
    && chown 1000:1000 "${JELLYFIN_WEB_DIR}" \
    && chmod 0775 "${JELLYFIN_WEB_DIR}"

ENTRYPOINT ["/usr/local/bin/jellyfinmod-entrypoint"]
