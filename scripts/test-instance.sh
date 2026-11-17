#!/usr/bin/env bash
# A local Jularr that stays: its own database on the isolated test server and its own folders, kept between runs. The Owner enters the real
# indexer, download server and VPN once in its interface; saved states are copied for every test, so a test never breaks the set-up.
#   scripts/test-instance.sh start [name]          runs the instance (default name: main) until Ctrl+C; http://127.0.0.1:8190 for main
#   scripts/test-instance.sh save <state> [name]   keeps the database and the data folder (keys, settings) of a stopped instance as <state>
#   scripts/test-instance.sh copy <state> <name>   makes instance <name> a fresh copy of <state> (an existing <name> is replaced)
#   scripts/test-instance.sh list                  shows the instances and the saved states
# Everything lives in ~/.local/share/jularr-test (never in the repository, never in CI): the states hold the entered keys, encrypted.
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
home="${HOME}/.local/share/jularr-test"
container="${JULARR_TEST_PG_CONTAINER:-jularr-test-pg}"
pg_user="jularr_test"
export PGPASSWORD="isolated-test-password"
mkdir -p "${home}/instances" "${home}/states"

database() { echo "jularr_instance_$1"; }
sql() { psql -h 127.0.0.1 -p 15433 -U "${pg_user}" -d jularr_test_admin -v ON_ERROR_STOP=1 -qAt -c "$1"; }
port() { if [[ "$1" == main ]]; then echo 8190; else echo $((8191 + $(printf '%s' "$1" | cksum | cut -d' ' -f1) % 800)); fi; }
running() { [[ -f "${home}/instances/$1/pid" ]] && kill -0 "$(cat "${home}/instances/$1/pid")" 2>/dev/null; }
valid() { [[ "$1" =~ ^[a-z0-9_]{1,40}$ ]] || { echo "Names are 1-40 characters a-z, 0-9 and _." >&2; exit 2; }; }

start() {
  local name="${1:-main}"; valid "${name}"
  local dir="${home}/instances/${name}" db; db="$(database "${name}")"
  running "${name}" && { echo "Instance ${name} already runs on http://127.0.0.1:$(port "${name}")."; exit 0; }
  mkdir -p "${dir}/data" "${dir}/storage"
  if [[ -z "$(sql "SELECT 1 FROM pg_database WHERE datname = '${db}'")" ]]; then
    sql "CREATE DATABASE \"${db}\""
    echo "New instance ${name}: sign up the Owner, then enter the indexer, the download server and the VPN."
  fi
  echo "Building the frontend and the host..."
  (cd "${repo}/src/Jularr.Frontend" && npm run build >/dev/null)
  dotnet build "${repo}/src/Jularr.Frontend" -c Release -m:2 -nodeReuse:false -p:UseSharedCompilation=false -v quiet -nologo >/dev/null
  # The tools of the live image: the pinned FFmpeg of CI, and cjxl (covers as JPEG XL) from the Debian package when it is not installed.
  eval "$("${repo}/scripts/local-ffmpeg.sh")"
  local tools="${HOME}/.cache/jularr-tools" cjxl; cjxl="$(command -v cjxl || true)"
  if [[ -z "${cjxl}" ]]; then
    cjxl="${tools}/jxl/root/usr/bin/cjxl"
    if [[ ! -x "${cjxl}" ]]; then
      rm -rf "${tools}/jxl" && mkdir -p "${tools}/jxl/deb"
      (cd "${tools}/jxl/deb" && apt-get download libjxl-tools >/dev/null && dpkg -x libjxl-tools_*.deb "${tools}/jxl/root")
    fi
  fi
  local address="http://127.0.0.1:$(port "${name}")"
  echo "Jularr ${name}: ${address} (Ctrl+C stops it; the database and the folders stay)"
  echo "Downloads and the library belong under ${dir}/storage; the log is ${dir}/host.log."
  cd "${repo}/src/Jularr.Frontend"
  ASPNETCORE_URLS="${address}" ASPNETCORE_ENVIRONMENT=Development \
  JULARR_DB="Host=127.0.0.1;Port=15433;Username=${pg_user};Password=${PGPASSWORD};Database=${db}" \
  DataPath="${dir}/data" Storage__MountRoot="${dir}/storage" Storage__RequireMountPoint=false Storage__LocalPaths__0="${dir}/storage" \
  Media__FfmpegPath="${JULARR_TEST_FFMPEG}" Media__FfprobePath="$(dirname "${JULARR_TEST_FFMPEG}")/ffprobe" Media__CjxlPath="${cjxl}" \
    dotnet bin/Release/net10.0/Jularr.Frontend.dll > >(tee -a "${dir}/host.log" | grep --line-buffered -E "fail|crit|Now listening") 2>&1 &
  echo $! > "${dir}/pid"
  trap 'kill -TERM "$(cat "${dir}/pid")" 2>/dev/null || true' INT TERM
  wait "$(cat "${dir}/pid")" || true
  rm -f "${dir}/pid"
  echo "Stopped ${name}."
}

save() {
  local state="${1:?state name}" name="${2:-main}"; valid "${state}"; valid "${name}"
  running "${name}" && { echo "Stop instance ${name} first (Ctrl+C), so its database is quiet." >&2; exit 1; }
  local out="${home}/states/${state}"
  rm -rf "${out}.tmp" && mkdir -p "${out}.tmp"
  docker exec "${container}" pg_dump -U "${pg_user}" -Fc "$(database "${name}")" > "${out}.tmp/database.dump"
  tar -C "${home}/instances/${name}" -czf "${out}.tmp/data.tgz" data
  rm -rf "${out}" && mv "${out}.tmp" "${out}"
  echo "Saved ${name} as state ${state} ($(du -sh "${out}" | cut -f1))."
}

copy() {
  local state="${1:?state name}" name="${2:?instance name}"; valid "${state}"; valid "${name}"
  local in="${home}/states/${state}" dir="${home}/instances/${name}" db; db="$(database "${name}")"
  [[ -d "${in}" ]] || { echo "No state ${state}." >&2; exit 1; }
  running "${name}" && { echo "Stop instance ${name} first." >&2; exit 1; }
  sql "DROP DATABASE IF EXISTS \"${db}\" WITH (FORCE)"
  sql "CREATE DATABASE \"${db}\""
  docker exec -i "${container}" pg_restore -U "${pg_user}" -d "${db}" --no-owner < "${in}/database.dump"
  rm -rf "${dir}/data" && mkdir -p "${dir}/storage"
  tar -C "${dir}" -xzf "${in}/data.tgz"
  echo "Instance ${name} is now a copy of ${state}: scripts/test-instance.sh start ${name}"
}

list() {
  echo "Instances:"; for d in "${home}"/instances/*/; do [[ -d "$d" ]] || continue; n="$(basename "$d")"
    echo "  ${n}  http://127.0.0.1:$(port "${n}")  $(running "${n}" && echo runs || echo stopped)"; done
  echo "States:"; for d in "${home}"/states/*/; do [[ -d "$d" ]] && echo "  $(basename "$d")  $(du -sh "$d" | cut -f1)"; done; true
}

case "${1:-}" in
  start) shift; start "$@" ;;
  save) shift; save "$@" ;;
  copy) shift; copy "$@" ;;
  list) list ;;
  *) sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac
