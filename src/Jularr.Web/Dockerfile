FROM debian:bookworm-slim AS whisper
# whisper.cpp publishes no checksum for its source archive. The pinned SHA-256 is
# the digest of the GitHub tag archive for v${WHISPER_CPP_VERSION}; update both
# values together when bumping the version.
ARG WHISPER_CPP_VERSION=1.9.4
ARG WHISPER_CPP_SHA256=57e280cee375ab02425b806ad5146b99f6eb9357e3c2b31357c8a6af2e2e44ae
RUN apt-get update \
    && apt-get install -y --no-install-recommends build-essential ca-certificates cmake curl tar \
    && rm -rf /var/lib/apt/lists/*
RUN mkdir -p /src/whisper \
    && curl -fsSL --retry 5 --retry-delay 2 --retry-all-errors \
       "https://github.com/ggml-org/whisper.cpp/archive/refs/tags/v${WHISPER_CPP_VERSION}.tar.gz" \
       -o /tmp/whisper.tar.gz \
    && { echo "${WHISPER_CPP_SHA256}  /tmp/whisper.tar.gz" | sha256sum -c - \
         || { echo "whisper.cpp v${WHISPER_CPP_VERSION} archive does not match the pinned SHA-256." >&2; exit 1; }; } \
    && tar -xzf /tmp/whisper.tar.gz -C /src/whisper --strip-components=1 \
    && cmake -S /src/whisper -B /src/whisper/build \
       -DBUILD_SHARED_LIBS=OFF \
       -DWHISPER_BUILD_TESTS=OFF \
       -DWHISPER_BUILD_EXAMPLES=ON \
       -DGGML_NATIVE=OFF \
    && cmake --build /src/whisper/build --config Release --parallel 2 --target whisper-cli \
    && test -x /src/whisper/build/bin/whisper-cli

FROM debian:bookworm-slim AS codex
# Pinned Codex CLI release package from https://github.com/openai/codex/releases.
# The SHA-256 values are the upstream codex-package_SHA256SUMS entries for
# rust-v${CODEX_VERSION} (identical to the GitHub release asset digests); update
# them together with CODEX_VERSION. Only bin/codex is installed.
ARG CODEX_VERSION=0.155.1
ARG CODEX_PACKAGE_SHA256_AMD64=a65b895c6ac1a73629bbe4b864640c86133e94a43b4d67b3103044e1a306d5a2
ARG CODEX_PACKAGE_SHA256_ARM64=71857dbc9bea3613410e8a69cfb46b07c0402d6d20fec18843dbaffd757634bd
ARG TARGETARCH
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl gzip tar \
    && rm -rf /var/lib/apt/lists/*
RUN set -eu; \
    case "${TARGETARCH:-amd64}" in \
        amd64) codex_target=x86_64-unknown-linux-musl; codex_sha256="${CODEX_PACKAGE_SHA256_AMD64}" ;; \
        arm64) codex_target=aarch64-unknown-linux-musl; codex_sha256="${CODEX_PACKAGE_SHA256_ARM64}" ;; \
        *) echo "Unsupported target architecture for the Codex CLI: ${TARGETARCH}" >&2; exit 1 ;; \
    esac; \
    codex_asset="codex-package-${codex_target}.tar.gz"; \
    curl -fsSL --retry 5 --retry-delay 2 --retry-all-errors \
        "https://github.com/openai/codex/releases/download/rust-v${CODEX_VERSION}/${codex_asset}" \
        -o "/tmp/${codex_asset}"; \
    echo "${codex_sha256}  /tmp/${codex_asset}" | sha256sum -c - \
        || { echo "Codex ${CODEX_VERSION} ${codex_asset} does not match the pinned SHA-256." >&2; exit 1; }; \
    mkdir -p /tmp/codex-package; \
    tar -xzf "/tmp/${codex_asset}" -C /tmp/codex-package bin/codex; \
    install -m 0755 /tmp/codex-package/bin/codex /codex; \
    rm -rf /tmp/codex-package "/tmp/${codex_asset}"; \
    /codex --version

FROM debian:bookworm-slim AS japanese-data
ARG JMDICT_VERSION=3.6.2+20260921173324
ARG JMDICT_GER_URL=https://github.com/scriptin/jmdict-simplified/releases/download/3.6.2%2B20260921173324/jmdict-ger-3.6.2%2B20260921173324.json.tgz
ARG JMDICT_ENG_URL=https://github.com/scriptin/jmdict-simplified/releases/download/3.6.2%2B20260921173324/jmdict-eng-common-3.6.2%2B20260921173324.json.tgz
ARG JMDICT_GER_SHA256=3a6c2cb1580ea845d87689e0982664dd1edc4cbc84426bba03e913e8fa6f81fc
ARG JMDICT_ENG_SHA256=db98447ee0b3918ac29f58c95d0b5b0b1e2aeb15f81fbe13d53ee20427028424
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl jq tar open-jtalk-mecab-naist-jdic \
    && rm -rf /var/lib/apt/lists/*
RUN test -f /var/lib/mecab/dic/open-jtalk/naist-jdic/sys.dic
RUN mkdir -p /dictionary /tmp/jmdict-ger /tmp/jmdict-eng \
    && curl -fsSL --retry 5 --retry-delay 2 --retry-all-errors \
       "${JMDICT_GER_URL}" \
       -o /tmp/jmdict-ger.tgz \
    && curl -fsSL --retry 5 --retry-delay 2 --retry-all-errors \
       "${JMDICT_ENG_URL}" \
       -o /tmp/jmdict-eng.tgz \
    && echo "${JMDICT_GER_SHA256}  /tmp/jmdict-ger.tgz" | sha256sum -c - \
    && echo "${JMDICT_ENG_SHA256}  /tmp/jmdict-eng.tgz" | sha256sum -c - \
    && tar -xzf /tmp/jmdict-ger.tgz -C /tmp/jmdict-ger \
    && tar -xzf /tmp/jmdict-eng.tgz -C /tmp/jmdict-eng \
    && GER_JSON="$(find /tmp/jmdict-ger -type f -name '*.json' -print -quit)" \
    && ENG_JSON="$(find /tmp/jmdict-eng -type f -name '*.json' -print -quit)" \
    && test -n "$GER_JSON" \
    && test -n "$ENG_JSON" \
    && jq -r '.words[] | . as $w | ([.sense[].gloss[].text] | unique | .[0:3] | join("; ") | gsub("[\\t\\r\\n]+"; " ")) as $meaning | ($w.kana[0].text // "") as $reading | (($w.kanji[]? | [.text, $reading, (if .common then "1" else "0" end), $meaning] | @tsv), ($w.kana[]? | [.text, .text, (if .common then "1" else "0" end), $meaning] | @tsv))' "$GER_JSON" > /dictionary/jmdict-ger.tsv \
    && jq -r '.words[] | . as $w | ([.sense[].gloss[].text] | unique | .[0:3] | join("; ") | gsub("[\\t\\r\\n]+"; " ")) as $meaning | ($w.kana[0].text // "") as $reading | (($w.kanji[]? | [.text, $reading, (if .common then "1" else "0" end), $meaning] | @tsv), ($w.kana[]? | [.text, .text, (if .common then "1" else "0" end), $meaning] | @tsv))' "$ENG_JSON" > /dictionary/jmdict-eng-common.tsv \
    && test -s /dictionary/jmdict-ger.tsv \
    && test -s /dictionary/jmdict-eng-common.tsv

FROM node:24-bookworm-slim AS frontend
WORKDIR /src
COPY package.json package-lock.json ./
COPY scripts/install-git-hooks.mjs scripts/
RUN npm ci
COPY vite.config.ts tsconfig.json ./
COPY scripts/ scripts/
COPY src/Jularr.Web/frontend/ src/Jularr.Web/frontend/
# Page-local TypeScript and SCSS are first-class Vite inputs. Keep the whole Pages tree
# available so later page migrations do not require Dockerfile changes.
COPY src/Jularr.Web/Pages/ src/Jularr.Web/Pages/
RUN npm run check

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Jularr.Web/Jularr.Web.csproj src/Jularr.Web/
RUN dotnet restore src/Jularr.Web/Jularr.Web.csproj

COPY design/player/ design/player/
COPY src/Jularr.Web/ src/Jularr.Web/
COPY --from=frontend /src/src/Jularr.Web/wwwroot/build/ src/Jularr.Web/wwwroot/build/
RUN dotnet publish src/Jularr.Web/Jularr.Web.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    CODEX_HOME=/data/codex
EXPOSE 8080
RUN set -eux; \
    for attempt in 1 2 3; do \
        rm -rf /var/lib/apt/lists/*; \
        apt-get update -o Acquire::Retries=5; \
        if apt-get install -y --no-install-recommends -o Acquire::Retries=5 ca-certificates curl ffmpeg libgomp1 libstdc++6; then \
            break; \
        fi; \
        if [ "$attempt" = "3" ]; then \
            exit 1; \
        fi; \
        sleep $((attempt * 3)); \
    done; \
    ffmpeg -version >/dev/null; \
    ffprobe -version >/dev/null; \
    rm -rf /var/lib/apt/lists/*; \
    if getent passwd app >/dev/null 2>&1; then \
        groupmod -n jularr app; \
        usermod -l jularr -g jularr -d /data -s /usr/sbin/nologin app; \
    else \
        groupadd --gid 1654 jularr; \
        useradd --uid 1654 --gid 1654 --home-dir /data --shell /usr/sbin/nologin jularr; \
    fi; \
    mkdir -p /data/codex /data/whisper /data/transcription-cache /opt/jularr-media/root; \
    rm -rf /media; \
    ln -s /opt/jularr-media/root /media; \
    chown -R jularr:jularr /data /opt/jularr-media
# Jularr runs as the dedicated non-root "jularr" user, fixed at UID/GID
# 1654:1654 (renamed from the aspnet base image's own "app" user, which
# already has that UID/GID; a fresh groupadd/useradd with the same fixed
# 1654:1654 is the fallback if a future base image drops "app"). The UID/GID
# are pinned so that a `chown` an operator has already run against them stays
# correct across rebuilds. Only /data (persistent state) and /tmp (scratch)
# are writable for it; the application, tools and dictionaries stay
# root-owned and read-only.
# Data volumes created by earlier root-based images are root-owned. The
# entrypoint refuses to start until /data is writable and prints the one-time
# ownership fix documented in docs/ADMIN_OPERATIONS.md; it never chowns itself.
COPY --chmod=0755 <<"EOF" /usr/local/bin/jularr-entrypoint
#!/bin/sh
set -eu

data_root=/data
runtime_uid="$(id -u)"
runtime_gid="$(id -g)"

fail_data_access() {
    cat >&2 <<MSG
Jularr startup aborted: $1
Jularr runs as the non-root user uid=${runtime_uid} gid=${runtime_gid} (jularr, fixed at 1654:1654) and needs read/write access to everything under ${data_root}.
Data volumes created by Jularr/AniLingo images that still ran as root are owned by root and must be handed over once.
Stop Jularr and run (replace <jularr-data> with the data volume name or host path mounted at ${data_root}):

  docker run --rm --user 0:0 --entrypoint chown -v <jularr-data>:/data ghcr.io/juloc/jularr:latest -R 1654:1654 /data

Then start Jularr again.
See "Non-root container runtime and /data ownership" in docs/ADMIN_OPERATIONS.md.
MSG
    exit 1
}

if [ ! -d "${data_root}" ]; then
    fail_data_access "${data_root} does not exist or is not a directory."
fi

blocked_path="$(find "${data_root}" \
    \( -type d \( ! -readable -o ! -writable -o ! -executable \) \) -print -quit \
    -o \( -type f \( ! -readable -o ! -writable \) \) -print -quit \
    2>/dev/null || true)"
if [ -n "${blocked_path}" ]; then
    fail_data_access "${blocked_path} is not readable and writable by the Jularr runtime user."
fi

configure_media_root() {
    source_path="${JULARR_MEDIA_ROOT:-}"
    [ -z "${source_path}" ] && return 0

    case "${source_path}" in
        /*) ;;
        *)
            echo "Jularr startup aborted: JULARR_MEDIA_ROOT must be an absolute container path." >&2
            exit 1
            ;;
    esac

    target_path=/opt/jularr-media/root

    if [ -L "${target_path}" ]; then
        if [ "$(readlink "${target_path}")" = "${source_path}" ]; then
            return 0
        fi
        rm "${target_path}"
    elif [ -d "${target_path}" ]; then
        if [ -n "$(find "${target_path}" -mindepth 1 -print -quit 2>/dev/null)" ]; then
            echo "Jularr startup aborted: the internal media gateway is not empty." >&2
            exit 1
        fi
        rmdir "${target_path}"
    else
        echo "Jularr startup aborted: the internal media gateway is invalid." >&2
        exit 1
    fi

    # Creating a symlink does not stat/dereference source_path. A sleeping or
    # stale network mount therefore cannot prevent the container from starting.
    ln -s "${source_path}" "${target_path}"
}

configure_media_root

exec dotnet /app/Jularr.Web.dll "$@"
EOF
# Windows checkouts with core.autocrlf add CR to the heredoc; normalize and syntax-check.
RUN sed -i 's/\r$//' /usr/local/bin/jularr-entrypoint \
    && sh -n /usr/local/bin/jularr-entrypoint
COPY --from=codex /codex /usr/local/bin/codex
COPY --from=whisper /src/whisper/build/bin/whisper-cli /usr/local/bin/whisper-cli
COPY --from=japanese-data /var/lib/mecab/dic/open-jtalk/naist-jdic /var/lib/mecab/dic/open-jtalk/naist-jdic
COPY --from=japanese-data /dictionary ./dictionary
COPY --from=build /app/publish .
USER jularr
ENTRYPOINT ["/usr/local/bin/jularr-entrypoint"]
