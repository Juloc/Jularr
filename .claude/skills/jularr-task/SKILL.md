---
name: jularr-task
description: Default Jularr workflow for bugs, backend work, issues and small/medium feature slices - owner first, smallest complete slice, focused tests, commit to dev, then stop. Use for any normal implementation; specialist skills extend it.
---

# Jularr task

One slice, one pass: understand, find the owner, implement completely, validate narrowly, commit, stop. Optimize for correct implementation per token without lowering quality.

## 1. Session setup (once per continuous session, not per slice)

- Read `AGENTS.md`, `.agent/project.yaml`, `docs/MAINTAINABILITY_CONVENTIONS.md` and the central `Juloc/agent-control` files `AGENTS.md`, `docs/ENGINEERING_RULES.md`, `docs/MAINTAINABILITY_CONVENTIONS.md`, `docs/PROTOCOL.md` (`gh api repos/Juloc/agent-control/contents/<path> -H "Accept: application/vnd.github.raw"`). Do not reread unchanged rules for the next slice.
- Environment: Windows, Git Bash + PowerShell, no `python`. PostgreSQL tests need Docker (data on the external D: drive, which must be connected); use the `jularr-dev-env` skill when Docker/the live site misbehaves.
- Branch: work from current `origin/dev` and push to `dev` (no PR per change, never touch `main`, never publish releases, never force push).

## 2. Understand (per slice)

Read only: the user request, the directly relevant issue (`gh issue view N --repo Juloc/Jularr --json title,body,comments`), the binding SPEC that owns the behavior, the canonical code owner and its directly related tests. Do not read all issues, docs, mockups, branches, PRs or worktrees, and do not reconstruct the architecture again. Once owner and files are known, stop broad searching; widen only when a concrete contradiction blocks implementation.

Modules for orientation: MediaCore (Work identity), Library/Storage (assets, files, LibraryRoot), Acquisition, Playback + PlaybackSessions, Progress, ReaderCore/ReaderPreferences, Discovery, Notifications under `src/Jularr.Web/Features/<Module>`.

## 3. Authority

Product behavior, highest first: current explicit user/product decision, newest binding SPEC, issue acceptance criteria and newer issue decisions, approved mockup (visual detail only), existing behavior.
Architecture ownership, highest first: `docs/DOMAIN.md`/`docs/ARCHITECTURE.md` canonical owner, binding subsystem SPEC, established current implementation owner.
A product decision may change behavior; it never authorizes a duplicate architecture. When a current explicit decision supersedes a SPEC, update that SPEC in the same slice so no contradictory rule remains.

## 4. Coordination (cheap, but real)

Claim the smallest necessary scope on the ledger (`Juloc/agent-control` issue #1 gives `currentLedgerIssue`; read only the last comment page, reduce events to unreleased unexpired claims). A claim is one comment:

```text
<!-- agent-control:event
{"version":1,"type":"claim","claimId":"claude-jularr-<topic>-<yyyymmdd>","agent":"claude","repository":"Juloc/Jularr","task":"#N","scope":["path/**"],"leaseMinutes":120,"leaseUntil":"<UTC RFC3339>"}
-->
```

Post with `gh api repos/Juloc/agent-control/issues/1/comments -f body=...`, re-read, the older claim wins on overlap. Within the same scope do not reread the ledger or reinventory branches/PRs/worktrees. Re-check only when the scope changes, `origin/dev` moved unexpectedly, concurrent work is detected, or before push. Release with a `release`/`done` event for the same `claimId` when finished.

Reconcile existing work only when triggered: relevant uncommitted work exists, a relevant PR/branch is known, overlap is detected, the user asks to reuse old work, or dev history shows conflicting concurrent changes. Then inspect just that source: PR diff, `git cherry origin/dev <branch>` (patch-id based; `main` history was rewritten 2026-09-29, so ahead/behind counts of old branches mislead) and `git status` of the relevant worktree. Port only what is still correct through the normal path; never blind-merge a stale branch. Never delete, reset, clean or switch away from uncommitted work.

## 5. Owner first

Before adding a service, manager, store, scheduler, provider, state machine, config path, table or helper abstraction, locate the canonical current owner and extend it. One responsibility, one owner; no second implementation because it is easier locally.

## 6. Smallest complete slice

Small is not "foundation only", "service exists but not wired" or "TODO later". Include whatever applies: runtime wiring, persistence, server-side auth, error behavior, restart/idempotency, UI/control-plane wiring and a regression test. Do not fix unrelated nearby problems: note them briefly and continue (a focused GitHub issue only when real and not already tracked).

Code rules: Microsoft naming, Allman braces, four spaces; calls, signatures, conditions and LINQ on one line when at most 230 characters (never above 280); no forwarding wrappers or artificial mini-methods; comments only for non-obvious intent; `CancellationToken` and real timeouts on I/O; no swallowed failures; no N+1 or application-side filtering of whole tables; DTOs across layers; no hidden mutation in reads; no dual-write, permanent fallback, commented-out code or TODO debris. When a new canonical path replaces an old one: migrate data once, switch reads and writes, delete the old runtime path, its tests and dead UI. Persistence: add `jularr-db-change`. UI: `jularr-ui`. Arr/acquisition: `jularr-acquisition`. Player/streaming: `jularr-playback-work`.

## 7. Test policy

Tests are required; broad validation during normal work is forbidden. Before every test command: name the exact changed production owner/files, pick the smallest tests that prove them, run only those.

- Target 1-5 directly relevant test classes, normally tens of tests, generally under ~100. If a command would likely run more than ~100 tests, stop and narrow it first. A genuinely cross-cutting shared contract may justify a larger run, but only as an explicit class list, never a broad project/category selector for confidence. Thousands of tests are never "focused".
- Never run `dotnet test Jularr.sln` as ordinary slice completion. A full suite is only for an explicit release gate, an explicit user request or a separately requested full validation. A focused failure may justify a somewhat broader targeted set, not an automatic full run.
- No huge background test runs, no second validation while one runs, no wait/monitor agents for tests, no rerunning an identical green command without relevant source change, no extra testing because the small tests were green.
- Order: smallest unit/behavior tests, directly affected integration tests, build when needed, then stop. Green focused validation means finish.
- Prefer behavior/regression tests (idempotent retry, restart recovery, authorization, state transitions) over tests of private wiring; PostgreSQL behavior is tested on real PostgreSQL; add a source/architecture guard when an invariant is objectively checkable.
- The `validation` list in `.agent/project.yaml` (restore, build, full test) is the release/explicit-request gate. When it was not run for a slice, say so in the final message instead of implying it passed.

## 8. Build and process discipline

Build the smallest meaningful target (e.g. the Web or Tests project); build the whole solution only when changes cross enough projects or an explicit final validation needs it. No automatic solution-wide restore after every slice, no needless Docker or dev-server restarts, never two dotnet builds at once. If `dotnet watch` locks the executable, handle it deliberately once instead of looping failed builds. Frontend assets changed: `npm ci` and the frontend checks.

## 9. Non-interactive rule

Never rely on the user answering firewall, sudo/password, OS security, package-install or browser-permission prompts. If a command triggers one: cancel it, do not wait or retry repeatedly, use an existing non-interactive local alternative, and record the limitation if relevant. Never weaken the firewall, TLS, certificate checks or OS security, and never create permanent firewall rules. Local dev servers bind loopback only (see `jularr-dev-env`).

## 10. Finish and stop

1. Run the review checklist on your own diff (`jularr-review`) and the maintainability completion gate for touched code.
2. `git fetch origin`; if `origin/dev` moved, rebase clean owned work onto it (never discard another agent's changes) and re-check the ledger before push.
3. Commit with the configured real identity. No agent attribution (`Co-Authored-By`, "Generated with") in commits, PRs, issues or source; never `--no-verify`; `npm ci` once so `.githooks` are active. Coherent focused commits.
4. `git push origin HEAD:dev`. If rejected, fetch and reconcile, never force.
5. Comment on the issue with what landed and what remains; tick or close only when every acceptance criterion is demonstrably met. Close or comment PRs whose work was incorporated or superseded.
6. Post `done`/`release` for the claim.
7. Stop. Do not pick another backlog issue. Only `jularr-overnight` overrides this stop rule.
