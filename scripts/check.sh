#!/usr/bin/env bash
# Local check before a push: what CI checks for the files changed against origin/main (commits and working tree).
#   backend:  build once, the smoke methods and test classes of ci-select-backend-tests.py in lanes (like the PR shards)
#   frontend: npm run check, npm test, UI resources and the Razor build (frontend.yml)
#   browser:  the Browser Smoke tests when its paths changed and Chromium is installed
# Tests use only the isolated test server 127.0.0.1:15433. Last line: "check: green" or "check: red (...)".
# CHECK_BASE=<ref> compares with another base, CHECK_LANES=<n> sets the parallel test processes (default 3).
set -uo pipefail

root="$(git rev-parse --show-toplevel)" || exit 2
cd "$root" || exit 2
base="${CHECK_BASE:-origin/main}"
lanes="${CHECK_LANES:-3}"
# English tool output, so the summary lines can be read.
export DOTNET_CLI_UI_LANGUAGE=en VSLANG=1033 DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
start=$SECONDS
failed=()
notes=()

say() { printf '%s\n' "$*"; }
# step NAME COMMAND...: runs quietly, shows the log tail only on failure.
step() {
  local name="$1" log="$work/${1//[^a-zA-Z0-9]/-}.log" t=$SECONDS
  shift
  if "$@" >"$log" 2>&1; then
    say "  ok   $name ($((SECONDS - t)) s)"
  else
    say "  FAIL $name ($((SECONDS - t)) s)"
    grep -E 'error|Failed |FAIL|Error' "$log" | grep -v ' 0 Error' | sort -u | head -n 25
    say "       log: $log"
    failed+=("$name")
  fi
}
finish() {
  local extra=""
  ((${#notes[@]})) && extra=" ($(IFS=';'; echo "${notes[*]}"))"
  if ((${#failed[@]})); then
    say "check: red ($(IFS=','; echo "${failed[*]}"))$extra"
    exit 1
  fi
  say "check: green$extra"
  exit 0
}

# Logs and test folders on the real disk: download storage refuses tmpfs and overlay folders.
export TMPDIR="${HOME}/.cache/jularr-tmp"
logs="${HOME}/.cache/jularr-check"
mkdir -p "$TMPDIR" "$logs"
# Logs of red runs stay a day for reading; older ones go. Not in TMPDIR: tests clean up there.
find "$logs" -mindepth 1 -maxdepth 1 -mmin +1440 -exec rm -rf {} + 2>/dev/null
work="$(mktemp -d "$logs/run.XXXXXX")"
trap 'rm -rf "$work/TestResults"' EXIT

git fetch --quiet origin main 2>/dev/null || notes+=("no fetch, compared with the local $base")
merge_base="$(git merge-base "$base" HEAD)" || { say "check: red (no merge base with $base)"; exit 1; }
# Committed, staged, unstaged and new files (CI compares the commits; this also sees what is not committed yet).
all="$( { git diff --name-only "$merge_base"; git ls-files --others --exclude-standard; } | sort -u)"
present="$(while IFS= read -r f; do [[ -n "$f" && -e "$f" ]] && printf '%s\n' "$f"; done <<< "$all")"

if [[ -z "$all" ]]; then
  say "check: green (no changes against $base)"
  exit 0
fi
say "$(wc -l <<< "$all") changed file(s) against $base"

# The same path rules as the workflows (backend-pr.yml, frontend.yml, browser-smoke.yml).
backend_files="$(grep -Ev '^(src/Jularr.Frontend/|tests/Jularr.E2E.Tests/)' <<< "$all" || true)"
backend=false frontend=false browser=false
grep -Eq '^(src/|tests/Jularr.Backend.Tests/|database/|scripts/|[^/]+[.]sln$|NuGet[.]Config$|[.]github/(actions/|workflows/backend(-pr)?[.]yml$))|[.](props|targets)$' \
  <<< "$backend_files" && backend=true
grep -Eq '^(src/Jularr.Frontend/|[.]github/workflows/frontend[.]yml$)' <<< "$all" && frontend=true
grep -Eq '^(src/Jularr.Frontend/|tests/Jularr.E2E.Tests/|database/|[.]github/workflows/browser-smoke[.]yml$)' <<< "$all" && browser=true
say "backend: $backend, frontend: $frontend, browser smoke: $browser"

step "repository hygiene" node scripts/verify-repository-hygiene.mjs
grep -Eq '^scripts/(verify-repository-hygiene|authorship)' <<< "$all" &&
  step "hygiene and authorship script tests" node --test scripts/verify-repository-hygiene.test.mjs scripts/authorship.test.mjs

frontend_dir=src/Jularr.Frontend
npm_ready() {
  # npm ci only when node_modules is missing or older than the lock file.
  if [[ ! -f "$frontend_dir/node_modules/.package-lock.json" || "$frontend_dir/package-lock.json" -nt "$frontend_dir/node_modules/.package-lock.json" ]]; then
    step "npm ci" npm --prefix "$frontend_dir" ci --no-audit --no-fund
  fi
}

if $frontend; then
  npm_ready
  step "npm run check" npm --prefix "$frontend_dir" run check
  step "npm test" npm --prefix "$frontend_dir" test
  step "UI resources" node "$frontend_dir/Tools/verify-ui-resources.mjs"
elif ($backend || $browser) && [[ ! -s "$frontend_dir/Assets/Generated/manifest.json" ]]; then
  # The Razor and browser tests need the compiled assets once per checkout.
  npm_ready
  step "frontend assets" npm --prefix "$frontend_dir" run build
fi

# One configuration for every build (Release, analyzers off like the CI test builds; they only warn), so nothing builds twice.
build() { dotnet build "$1" --configuration Release -p:RunAnalyzers=false -nologo -clp:ErrorsOnly; }
if $backend; then
  step "build backend tests" build tests/Jularr.Backend.Tests
elif $frontend; then
  step "build frontend" build "$frontend_dir/Jularr.Frontend.csproj"
fi
$browser && step "build browser tests" build tests/Jularr.E2E.Tests

# Only the isolated test server, whatever the environment says.
export JULARR_TEST_DB='Host=127.0.0.1;Port=15433;Username=jularr_test;Password=isolated-test-password;Database=jularr_test_admin'
test_db_up() { (exec 3<>/dev/tcp/127.0.0.1/15433) 2>/dev/null; }
blame=(--blame-hang-timeout 5m --blame-hang-dump-type mini --blame-crash --blame-crash-dump-type mini)

if $backend && ((${#failed[@]} == 0)); then
  if grep -Eq '^(scripts/ci-|tests/ci-durations[.]json$)' <<< "$all"; then
    step "CI script self-tests" bash -c 'python3 scripts/ci-shard-tests.py --self-test && python3 scripts/ci-select-backend-tests.py --self-test && python3 scripts/ci-classify-failure.py --self-test'
  fi
  if ! test_db_up; then
    say "  FAIL test database: nothing listens on 127.0.0.1:15433 (start jularr-test-pg, see docs/DEVELOPMENT.md)"
    failed+=("test database")
  else
    # The selection of ci-select-backend-tests.py for the files that exist now (it diffs commits only).
    selection="$(python3 -c '
import importlib.util, json, sys
spec = importlib.util.spec_from_file_location("selector", "scripts/ci-select-backend-tests.py")
selector = importlib.util.module_from_spec(spec); spec.loader.exec_module(selector)
paths = [line for line in sys.stdin.read().splitlines() if line]
_, classes = selector.choose(paths)
methods = [m for m in selector.SMOKE if m.split(".", 1)[0] not in classes]
print(json.dumps({"methods": "|".join(selector.method_clause(m) for m in methods), "classes": list(classes),
                  "exclude": selector.pr_exclude()}))' <<< "$present")"
    classes="$(jq -r '.classes | join(",")' <<< "$selection")"
    say "  tests: smoke methods${classes:+ + ${classes//,/, }}"
    filters=("$(jq -r '.methods' <<< "$selection")")
    planned=()
    if [[ -n "$classes" ]]; then
      # The lanes of ci-shard-tests.py as in one PR shard: measured weights, real-tool and Full-only tests left out.
      python3 scripts/ci-shard-tests.py matrix --kind backend --lanes "$lanes" --shards 1 --classes "$classes" \
        --exclude "$(jq -r '.exclude' <<< "$selection")" --out "$work/plan.json" >"$work/plan.log" 2>&1 &&
        mapfile -t planned < <(python3 scripts/ci-shard-tests.py lanes --plan "$work/plan.json" --shard 1/1 2>>"$work/plan.log")
      if ! grep -q . < <(printf '%s\n' "${planned[@]}"); then
        say "  FAIL test plan: ci-shard-tests.py planned no lane (log: $work/plan.log)"
        failed+=("test plan")
      fi
    fi
    filters+=("${planned[@]}")
    # Parallel test processes share one ready runtime directory (see backend-pr.yml).
    export XDG_RUNTIME_DIR="$work/runtime"
    install -d -m 0700 "$XDG_RUNTIME_DIR" "$XDG_RUNTIME_DIR/.dotnet" "$XDG_RUNTIME_DIR/.dotnet/shm"
    pids=() t=$SECONDS
    for index in "${!filters[@]}"; do
      [[ -n "${filters[$index]}" ]] || continue
      dotnet test tests/Jularr.Backend.Tests --configuration Release --no-build "${blame[@]}" --filter "${filters[$index]}" \
        --results-directory "$work/TestResults" >"$work/lane$index.log" 2>&1 &
      pids+=("$!:$index")
    done
    lane_failed=0
    for entry in "${pids[@]}"; do
      wait "${entry%%:*}" && continue
      lane_failed=1
      log="$work/lane${entry#*:}.log"
      grep -E '^\s+(Failed|Error Message:)|^\s+Assert|^error' -A2 "$log" | head -n 40
      say "       log: $log"
    done
    passed="$(cat "$work"/lane*.log | grep -Eo 'Passed: +[0-9]+' | grep -Eo '[0-9]+' | awk '{ n += $1 } END { print n + 0 }')"
    if ((lane_failed)); then
      say "  FAIL backend tests ($((SECONDS - t)) s)"
      failed+=("backend tests")
    else
      say "  ok   backend tests: ${passed:-0} passed in ${#pids[@]} process(es) ($((SECONDS - t)) s)"
    fi
  fi
fi

if $browser && ((${#failed[@]} == 0)); then
  if ! compgen -G "$HOME/.cache/ms-playwright/chromium-*" >/dev/null; then
    say "  SKIP browser smoke: no Playwright Chromium in ~/.cache/ms-playwright (CI runs it)"
    notes+=("browser smoke not run: Chromium missing")
  elif ! test_db_up; then
    say "  FAIL browser smoke: nothing listens on 127.0.0.1:15433"
    failed+=("test database")
  else
    smoke="$(grep -Eo 'FullyQualifiedName~FreshInstance[^"]*' .github/workflows/browser-smoke.yml)"
    step "browser smoke" dotnet test tests/Jularr.E2E.Tests --configuration Release --no-build "${blame[@]}" \
      --filter "$smoke" --results-directory "$work/TestResults"
  fi
fi

say "time: $((SECONDS - start)) s"
((${#failed[@]})) || rm -rf "$work"
finish
