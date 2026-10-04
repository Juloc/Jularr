---
name: jularr-start-task
description: Startup procedure before substantial Jularr work - read rules, sync dev, inspect issues/PRs/branches/worktrees, resolve the coordination ledger, claim scope, identify the canonical owner and binding specs. Use at the start of any implementation slice.
---

# Jularr start-task

1. **Rules** (once per session): `AGENTS.md`, `.agent/project.yaml`, `.agent/upgrade-policy.yaml`, `docs/MAINTAINABILITY_CONVENTIONS.md`, and the canonical `Juloc/agent-control` files `AGENTS.md`, `docs/ENGINEERING_RULES.md`, `docs/MAINTAINABILITY_CONVENTIONS.md`, `docs/PROTOCOL.md` (`gh api repos/Juloc/agent-control/contents/<path> -H "Accept: application/vnd.github.raw"`).
2. **Sync**: `git fetch --all --prune`; fast-forward `dev` to `origin/dev`. Normal work lands directly on `dev`; never touch `main`; never publish releases.
3. **Existing work**: run `jularr-reconcile-existing-work` for the target area before writing code (open PRs, `git cherry origin/dev <branch>` for unique patches, every worktree's `git status`). Never reset, clean or delete a worktree with uncommitted work.
4. **Issue**: read the full issue and its comments (`gh issue view N --repo Juloc/Jularr --json title,body,comments`). Check dependencies and whether current `dev` already satisfies it. `docs/implementation/request-to-play-readiness-audit.md` is the current map of the video vertical.
5. **Ledger**: `Juloc/agent-control` issue #1 holds `currentLedgerIssue` (currently #1 itself with 1800+ comments, so read only the last pages: `repos/Juloc/agent-control/issues/1/comments?per_page=100&page=N`). Reduce events to unreleased claims (claim minus release/done) and drop expired `leaseUntil`. A claim is one issue comment:

   ```text
   <!-- agent-control:event
   {"version":1,"type":"claim","claimId":"claude-jularr-<topic>-<yyyymmdd>","agent":"claude","repository":"Juloc/Jularr","task":"#N","scope":["path/**"],"leaseMinutes":120,"leaseUntil":"<UTC RFC3339>"}
   -->
   ```

   Post with `gh api repos/Juloc/agent-control/issues/1/comments -f body=...`, immediately re-read; the older claim wins on overlap. Use the smallest useful scope. Re-check before every push. End with a `release` or `done` event for the same `claimId`.
   A claim that predates a dedicated run is stale only when the user said so. Fresh `dev` commits by another agent mean live concurrent work: coordinate, never overwrite.
6. **Owner**: search for the existing owner before adding any service/store/helper (`src/Jularr.Web/Features/<Module>`). Modules: MediaCore (Work identity), Library/Storage (assets, files, roots), Acquisition (request/wanted/download/import), Playback + PlaybackSessions, Progress, ReaderCore/ReaderPreferences, Discovery, Notifications.
7. **Specs**: for UI read the complete `docs/mockups/<surface>/SPEC.md` and open the images (filenames are random); see `jularr-ui-from-spec`.
8. **Environment**: Windows, Git Bash + PowerShell, no `python`. PostgreSQL tests need Docker, and Docker data lives on the external D: drive, which must be connected.
