#!/usr/bin/env bash
# The same pinned jellyfin-ffmpeg build as CI (.github/actions/install-tools/action.yml, checked by its sha256), unpacked into ~/.cache/jularr-tools/ffmpeg
# without root. Prints the lines to export; the playback, music and transcoding tests then run locally instead of only in CI.
#   eval "$(scripts/local-ffmpeg.sh)"
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
action="${repo}/.github/actions/install-tools/action.yml"
version="$(sed -n 's/^ *FFMPEG_VERSION: *//p' "${action}" | head -1)"
sha="$(sed -n 's/^ *FFMPEG_SHA256: *//p' "${action}" | head -1)"
[[ -n "${version}" && -n "${sha}" ]] || { echo "FFMPEG_VERSION or FFMPEG_SHA256 not found in ${action}" >&2; exit 1; }

tools="${HOME}/.cache/jularr-tools/ffmpeg"
target="${tools}/${version}"
if [[ ! -x "${target}/ffmpeg" || ! -x "${target}/ffprobe" ]]; then
  name="jellyfin-ffmpeg_${version}_portable_linux64-gpl.tar.xz"
  archive="${tools}/${name}"
  mkdir -p "${tools}"
  if [[ ! -f "${archive}" ]] || ! echo "${sha}  ${archive}" | sha256sum -c --quiet - 2>/dev/null; then
    rm -f "${archive}"
    curl -fsSL --retry 3 --connect-timeout 20 --max-time 300 "https://github.com/jellyfin/jellyfin-ffmpeg/releases/download/v${version}/${name}" -o "${archive}" >&2
    echo "${sha}  ${archive}" | sha256sum -c --quiet - || { rm -f "${archive}"; echo "The download does not match its sha256." >&2; exit 1; }
  fi
  unpacked="$(mktemp -d)"
  tar -xJf "${archive}" -C "${unpacked}"
  mkdir -p "${target}"
  install -m 0755 "$(find "${unpacked}" -type f -name ffmpeg -print -quit)" "${target}/ffmpeg"
  install -m 0755 "$(find "${unpacked}" -type f -name ffprobe -print -quit)" "${target}/ffprobe"
  rm -rf "${unpacked}" "${archive}"
fi

echo "export JULARR_TEST_FFMPEG='${target}/ffmpeg'"
echo "export PATH='${target}':\"\$PATH\""
