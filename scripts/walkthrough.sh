#!/usr/bin/env bash
# The fixed walkthrough before a PR: screenshots of the main pages at 375, 820 and 1440 px, plus console errors, failed requests,
# sideways scroll and load time per page. Look at the pictures before the PR. Local only, never in CI.
#   scripts/walkthrough.sh            copies the test-instance state "eingerichtet" to instance "walk", starts it, walks, removes it
#   scripts/walkthrough.sh <address>  walks an already running Jularr (for example http://127.0.0.1:8190)
# Screenshots and summary.md go to ~/.local/share/jularr-test/walkthrough/<time>/; the last 3 runs are kept.
# The sign-in comes from ~/.local/share/jularr-test/instances/main/owner.txt (WALKTHROUGH_OWNER overrides it) and is never printed.
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
home="${HOME}/.local/share/jularr-test"
owner="${WALKTHROUGH_OWNER:-${home}/instances/main/owner.txt}"
out="${home}/walkthrough/$(date +%Y%m%d-%H%M%S)"
[[ -r "${owner}" ]] || { echo "No sign-in file ${owner}." >&2; exit 2; }

address="${1:-}"
cleanup() { :; }
if [[ -z "${address}" ]]; then
  name=walk
  address="http://127.0.0.1:$((8191 + $(printf '%s' "${name}" | cksum | cut -d' ' -f1) % 800))"
  [[ -d "${repo}/src/Jularr.Frontend/node_modules" ]] || (cd "${repo}/src/Jularr.Frontend" && npm ci --no-audit --no-fund >/dev/null)
  "${repo}/scripts/test-instance.sh" copy eingerichtet "${name}" >/dev/null
  setsid "${repo}/scripts/test-instance.sh" start "${name}" >"${home}/instances/${name}.start.log" 2>&1 &
  starter=$!
  cleanup() {
    local pid="${home}/instances/${name}/pid"
    if [[ -f "${pid}" ]]; then
      local host; host="$(cat "${pid}")"
      kill -TERM "${host}" 2>/dev/null || true
      # The host stops its workers first; after 60 s it is stopped hard.
      for _ in $(seq 1 60); do kill -0 "${host}" 2>/dev/null || break; sleep 1; done
      kill -KILL "${host}" 2>/dev/null || true
    fi
    wait "${starter}" 2>/dev/null || true
    PGPASSWORD=isolated-test-password psql -h 127.0.0.1 -p 15433 -U jularr_test -d jularr_test_admin -qAt \
      -c "DROP DATABASE IF EXISTS \"jularr_instance_${name}\" WITH (FORCE)" >/dev/null || true
    rm -rf "${home:?}/instances/${name}" "${home}/instances/${name}.start.log"
  }
  trap cleanup EXIT
  echo "Starting instance ${name} on ${address} (builds first)..."
  for _ in $(seq 1 900); do
    curl -fsS -o /dev/null "${address}/Account/Login" 2>/dev/null && break
    kill -0 "${starter}" 2>/dev/null || { tail -n 20 "${home}/instances/${name}.start.log" >&2; exit 1; }
    sleep 1
  done
fi

mkdir -p "${out}"
status=0
dotnet run "${repo}/scripts/walkthrough.cs" -- "${address}" "${owner}" "${out}" || status=$?
# Keep the last 3 runs.
ls -1dt "${home}"/walkthrough/*/ 2>/dev/null | tail -n +4 | xargs -r rm -rf
exit "${status}"
