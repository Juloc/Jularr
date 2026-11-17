#!/usr/bin/env bash
# A local Jularr to click through, with a catalog and library in the live proportions (tests/Jularr.E2E.Tests/DevInstance.cs), on the isolated test database.
# It builds what is needed, prints the address and the sign-in, and runs until Ctrl+C; the instance and its database are removed then.
#   scripts/dev-instance.sh            (JULARR_DEV_HOURS=12 by default)
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
export TMPDIR="${TMPDIR:-${HOME}/.cache/jularr-tmp}"
mkdir -p "${TMPDIR}" "${HOME}/.cache/jularr-dev"
export JULARR_TEST_DB="${JULARR_TEST_DB:-Host=127.0.0.1;Port=15433;Username=jularr_test;Password=isolated-test-password;Database=jularr_test_admin}"
export JULARR_DEV_INSTANCE=1

# Real files that play need FFmpeg: the pinned build of CI, fetched once.
if [[ -z "${JULARR_TEST_FFMPEG:-}" ]]; then eval "$("${repo}/scripts/local-ffmpeg.sh")"; fi

echo "Building the frontend and the browser tests..."
(cd "${repo}/src/Jularr.Frontend" && npm run build >/dev/null)
dotnet build "${repo}/tests/Jularr.E2E.Tests" -m:2 -nodeReuse:false -p:UseSharedCompilation=false -v quiet -nologo >/dev/null

stop="${HOME}/.cache/jularr-dev/stop"
log="${HOME}/.cache/jularr-dev/instance.log"
rm -f "${stop}"
# Its own session, so Ctrl+C reaches only this script: the instance then ends by itself and removes its database, nothing is left running.
setsid dotnet test "${repo}/tests/Jularr.E2E.Tests" --no-build --filter "FullyQualifiedName~DevInstance" --logger "console;verbosity=normal" >"${log}" 2>&1 &
pid=$!
trap 'touch "${stop}"; echo; echo "Stopping: the instance and its database are removed..."' INT TERM
tail -n +1 -f "${log}" --pid "${pid}" | grep --line-buffered -E "Jularr läuft|Anmeldung|Echte Dateien|Server-Log|Beenden|Failed|Fehlermeldung|error" &
while kill -0 "${pid}" 2>/dev/null; do wait "${pid}" 2>/dev/null || true; done
echo "Stopped."
