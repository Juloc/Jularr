# clean-start CI gates

The ordinary PR gate is deliberately bounded. The full test suite is retained,
not deleted. The current baseline timings are recorded in GitHub Actions'
`backend-test-timings` / `e2e-test-timings-*` artifacts.

| Workflow | PRs | After merge into `clean-start` | Purpose |
| --- | --- | --- | --- |
| `Backend / test` | Always reported; skipped on docs-only PRs | No | Build backend and all backend test sources; API, authentication, PostgreSQL bootstrap and module smoke tests |
| `Frontend / razor-and-ui-resources` | When frontend/backend UI files change | Yes, matching paths | Frontend typecheck, JS tests, localization, Razor build and publish verification |
| `Browser Smoke / smoke` | Frontend, E2E tests or schema changed | No | Fresh owner setup and sign-in in real Chromium |
| `Image / minimal-compose` | Docker/Compose/packaging or database schema changed | Yes, including source changes | Build the shipped container and start the supplied Compose stack |
| `Backend Full / test` and `e2e (...)` | Only when explicitly opted in | Every push | Entire backend and browser suite, real FFmpeg, JPEG XL and backup-tool regression |

## Full validation before a release

The full tests are not replaced by smoke tests. A release needs the full
backend/Playwright regression on the release commit, the shipped image smoke
check and the commands in [`.agent/project.yaml`](../.agent/project.yaml).
Investigate failures; do not mark a release validated from PR smoke tests
alone. New tests are compiled by Fast CI, but not all executed until Full CI.

For PR-specific integration validation, add the `ci/full` label to the PR.
`Backend Full` will run both heavy jobs on that PR; removing the label
returns later PR revisions to fast checks. Alternatively, run `Backend Full`
via `workflow_dispatch` once the workflow is registered on the repository's
default branch. Keep the focused test evidence in the PR description.

## Branch protection and status checks

- Keep `Backend / test` as the stable PR compile/smoke check, and
  `Backend / changes` to surface a failed path-classification step.
- `Authorship / authorship` remains unchanged.
- Do not require old `Backend / e2e (...)` checks on *every* PR.
  They are only emitted for opted-in Full CI.
- `Frontend`, `Browser Smoke` and `Image` are path-filtered workflows;
  do not require their individual contexts unconditionally for docs-only PRs.
- Check required-status rules in GitHub after merging this change. Workflow
  YAML cannot update rulesets or branch protection by itself.

## Nightly limitation

The repository's default branch is `main`; GitHub scheduled Actions execute
**only from the default branch**. A `schedule` entry on `clean-start`
would run against `main` (once present there), not against `clean-start`.
Therefore this PR does **not** claim to have enabled a working
`clean-start` nightly run. The full suite is automatic after each
`clean-start` push, and PRs can opt into it with `ci/full`.
A future default-branch dispatcher can schedule a run at `clean-start`
once that change is explicitly approved.

## Performance and failure handling

- Targets, not guarantees: backend PR smoke approximately 2–5 minutes,
  browser smoke approximately 3–5 minutes; package smoke is conditional.
- The full suite can take 20–30+ minutes and never blocks normal PR feedback.
- Never hide a failed check by changing its filter or marking it successful.
  Store TRX reports, and keep the affected focused tests in the agent PR notes.
- Any change that affects migrations, downloads, permissions, storage or
  acquisition needs focused integration tests *before* merge even when Fast CI
  itself is green. Use `ci/full` if the risk merits the complete regression.
