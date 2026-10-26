# CI gates for main and dev

The four workflow `setup-dotnet` calls and the shared `restore-build` action pin
SDK `10.0.403` explicitly. This avoids a latest-channel `aka.ms` redirect to an
unavailable download origin. Update the version in Backend, Backend Full,
Frontend, Browser Smoke and `restore-build` together and verify their normal gates.

The ordinary PR gate is deliberately bounded. An opted-in Full PR skips the
fast Backend build; its stable `Backend / test` gate waits for the reusable Full
regression instead of compiling and testing the backend twice. The full suite is retained,
not deleted. Per-test timings are kept in the `timings-*` artifacts of every run
and, as class totals, in [`tests/ci-durations.json`](../tests/ci-durations.json).

| Workflow | PRs | Push to `main` / `dev` | Purpose |
| --- | --- | --- | --- |
| `Backend / prepare` + `backend (n/2)` + `test` | Required `test` stays; drafts, docs-only and frontend/E2E-only changes skip the backend build | No | Build once, smoke methods, and affected backend tests in two shards |
| `Frontend / razor-and-ui-resources` | When frontend/backend UI files change | Yes, matching paths | Frontend typecheck, JS tests, localization, Razor build and publish verification |
| `Browser Smoke / smoke` | Frontend, E2E tests or schema changed | No | Fresh owner setup and sign-in in real Chromium |
| `Image / minimal-compose` | Docker/Compose/packaging or database schema changed | Yes, including source changes | Build the shipped container and start the supplied Compose stack |
| `Backend Full` (`smoke`, `backend (n/4)`, `e2e (n/4)`, `gate`) | Only as opted-in reusable `Backend / full-regression` job | Every push to `main` | Entire backend and browser suite, real FFmpeg, JPEG XL and backup-tool regression |

PR checks are scoped to PRs targeting `main` or `dev`; ordinary feature-branch pushes do not start Actions. Draft PRs retain the lightweight `Backend / test` and authorship checks, while builds, browser smoke, image smoke and full regression wait until `ready_for_review`. Pure frontend/E2E changes no longer run the backend compilation; dedicated frontend/browser jobs cover those areas. `Backend Full` is a reusable job within `Backend` for opt-in PRs, so ordinary PRs create no separate skipped full workflow run.

The `Release` workflow runs only for a `v*` tag (or by hand as a dry run) and is described in [RELEASE.md](RELEASE.md). TV builds, TV tests and TV lint run only if TV code or its shared Android dependencies changed since the last signed TV APK. [Android TV CI](../.github/workflows/android.yml) and [Android Mobile CI](../.github/workflows/android-mobile.yml) are separately path-filtered; server-only changes do not start either.

## Backend Full: fast and fail-fast

No job is meant to run longer than about 10 minutes. The run is a graph, not one long job:

```
smoke (build once + critical tests + frontend tests, ~5 min)
  -> 8 shards in ONE matrix: backend 1/4..4/4 (two test processes each) and e2e 1/4..4/4
  -> gate
```

- `smoke` compiles both test projects once, runs startup/schema/migration/session/setup-gate
  tests (`ci-select-backend-tests.py --boot-filter`; the upgrade test needs the PostgreSQL 18
  client, so smoke runs `install-tools` too) and `npm test`, then publishes the compiled
  workspace as the `jularr-build` artifact (about 220 MB, 1 day). Shards only unpack it
  (`.github/actions/restore-build`) and run `dotnet test --no-build`; none compiles.
- Browser jobs install Chromium through `.github/actions/install-chromium`: the browser is downloaded
  (cached), `ldd` checks that the runner image already has its system libraries (it ships Chrome), and
  only a missing library takes the slow apt route (hardened, three timed attempts). Plain apt at job
  time stalled the browser shards for ten minutes on slow mirrors.
- The shards are one matrix with `fail-fast: true`: the first failing shard cancels all others,
  and a newer push to the same branch or PR cancels the whole older run (`concurrency`).
- `gate` needs `smoke` and all shards and is the one conclusion of the run (the release guard
  reads the workflow conclusion of `Backend Full`; it is not required on every PR).
- Runner capacity: at most 8 jobs run at the same time (1 smoke, then 8 shards, then the
  light `gate`); every shard has its own PostgreSQL service container.
- Shards are made by `scripts/ci-shard-tests.py`: every `[TestClass]` (found in the sources) is
  packed longest-first into the lightest shard by its measured duration in
  `tests/ci-durations.json`. A class with no measurement weighs as the median class. Changing a
  shard count means editing that kind's matrix entries (`1/N` ... `N/N`); the self-check in `smoke`
  fails when a list is incomplete. Backend shard 4 also runs the three real-tool tests (FFmpeg,
  cjxl) and is planned lighter (`--reserve 4=150`).
- The PR gate (`Backend`) uses the same build and shard script: `prepare` runs the smoke methods and the
  selection, `backend (1/2)` and `backend (2/2)` split the selected classes, `test` is the required check.

Which shard failed: the job name says `backend (3/4)` or `e2e (2/4)`; the red lines are
prefixed `[lane1]` / `[lane2]` for the two test processes of a backend shard; browser evidence
is in the `e2e-evidence-*` artifact. Timings of a run: `gh run view <id> --json jobs --jq '.jobs[] | [.name,.startedAt,.completedAt] | @tsv'`.

Run one shard locally (needs the test PostgreSQL, see `.agent/project.yaml`):

```
dotnet build tests/Jularr.Backend.Tests -c Release
dotnet test tests/Jularr.Backend.Tests -c Release --no-build \
  --filter "$(python3 scripts/ci-shard-tests.py plan --kind backend --shard 2/4)"
python3 scripts/ci-shard-tests.py table --kind backend --shards 4 --lanes 2   # expected balance
```

Refresh the durations after a fully green run (class totals; renamed or new classes are
picked up, unknown classes weigh as the median until then):

```
gh run download <run-id> -p "timings-*" -D timings
python3 scripts/ci-shard-tests.py refresh timings
```

Do this when shards drift apart (compare the job times) or after many new tests; commit the file.

## Full validation before a release

The full tests are not replaced by smoke tests. A release needs the full
backend/Playwright regression on the release commit, the shipped image smoke
check and the commands in [`.agent/project.yaml`](../.agent/project.yaml).
Investigate failures; do not mark a release validated from PR smoke tests
alone. New tests are compiled by Fast CI, but not all executed until Full CI.

For PR-specific integration validation, add the `ci/full` label to the PR,
or put `[full-ci]` in its title. For ready PRs, `Backend / full-regression` invokes the reusable `Backend Full` graph. Removing the label/title marker returns later PR revisions to fast
checks. The title marker works without creating a repository label. The standalone `Backend Full` workflow remains available on `main` pushes or manual `workflow_dispatch`. Keep focused test evidence in the PR.

## Branch protection and status checks

- Keep `Backend / test` as the stable required PR check. It is the final job that needs
  `prepare` and the shards (a skipped shard job is fine when no test class was selected);
  path classification is a step in `prepare`: errors fail the required check instead of skipping it.
- `Authorship / authorship` remains unchanged.
- Do not require `Backend Full` checks (`smoke`, `backend (n/4)`, `e2e (n/4)`, `gate`) on *every* PR.
  They are only emitted for opted-in Full CI.
- `Frontend`, `Browser Smoke` and `Image` are path-filtered workflows;
  do not require their individual contexts unconditionally for docs-only PRs.
- Check required-status rules in GitHub after merging this change. Workflow
  YAML cannot update rulesets or branch protection by itself.

## Nightly limitation

The repository's default branch is `main`; GitHub scheduled Actions execute
**only from the default branch**. A `schedule` entry on `main`
would run against `main` (once present there), not against `main`.
Therefore this PR does **not** claim to have enabled a working
`main` nightly run. The full suite is automatic after each
`main` push, and PRs can opt into it with `ci/full`.
A future default-branch dispatcher can schedule a run at `main`
once that change is explicitly approved.

## Performance and failure handling

- Targets, not guarantees: backend PR smoke and targeted module suites
  approximately 2–5 minutes on ordinary changes; browser smoke 3–5 minutes.
  Larger changes can take longer; the package smoke is conditional.
- The full suite takes about 15 minutes in total (smoke, then the longest shard) and never blocks normal PR feedback.
  When several agents merge changes rapidly, newer `main` pushes cancel
  obsolete branch validation runs; the newest branch revision still receives
  Full CI, Frontend and container checks. Manual Full CI is not cancelled.
- Never hide a failed check by changing its filter or marking it successful.
  Store TRX reports, and keep the affected focused tests in the agent PR notes.
- `scripts/ci-select-backend-tests.py` maps acquisition, storage, authentication,
  media, reader, speech and other affected backend areas to actual test classes;
  newly changed `*Tests.cs` files also run their class. Shared unclassified
  backend code falls back to service and SQL tests. Its self-check runs in CI.
- Changes to migrations, downloads, permissions, storage or acquisition still
  require focused integration test evidence *before* merge. Use `ci/full` when
  a complete regression is warranted, and do not claim smoke tests suffice.
