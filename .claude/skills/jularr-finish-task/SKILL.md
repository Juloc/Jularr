---
name: jularr-finish-task
description: Completion procedure for a Jularr slice - sync dev, validate (restore/build/test), authorship-safe commit, push to dev, truthful issue/PR updates, claim release. Use before declaring any slice done.
---

# Jularr finish-task

1. Re-run the maintainability completion gate on the touched code (see `jularr-review`).
2. `git fetch origin` and re-read live claims (see `jularr-start-task`). If `origin/dev` moved, rebase clean owned work (or merge), resolve against the newer canonical code, and never discard another agent's changes.
3. Validate from the repo root and never run two dotnet builds at once:
   - `dotnet restore Jularr.sln`
   - `dotnet build Jularr.sln --no-restore`
   - `dotnet test Jularr.sln --no-build` (needs Docker/PostgreSQL; use `--filter` while iterating and a full run before completion).
   If frontend assets changed run `npm ci` and the frontend checks; if migrations changed confirm there is no PendingModelChanges.
4. Commit with the configured real identity. **No** `Co-Authored-By`, "Generated with" or any agent attribution in commits, PRs, issues or source; the hooks and CI reject it. Never `--no-verify`. Run `npm ci` once so `.githooks` are active. Keep commits coherent and focused.
5. Push directly to `dev` (`git push origin dev`): no PR per change, no force push, no release publishing, no `main` edits. If rejected, fetch and reconcile; never force.
6. GitHub: comment on the issue with what landed and what remains; tick or close only when every acceptance criterion is truly met. Close or comment PRs whose work was incorporated or superseded.
7. Post a `done` (or `release`) event for the claim on the ledger.
