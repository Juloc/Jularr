# Jularr agent instructions

## Start here

- Follow the current user request; explicit owner decisions override stale plans and legacy behavior.
- Read [conventions](docs/CONVENTIONS.md) once per continuous session before implementation.
- For Frontend work, read [Frontend conventions](docs/FRONTEND_CONVENTIONS.md), [Frontend CODEMAP](src/Jularr.Frontend/CODEMAP.md), and [UX decisions](docs/FRONTEND_UX.md) for changes to page behavior or appearance.
- Use [CODEMAP](docs/CODEMAP.md) to find the relevant owner and focused tests. Read only the needed files and linked specifications; use targeted `rg` searches.
- Follow [documentation authority](docs/DOCUMENTATION_POLICY.md) to distinguish current rules from historical plans. No Agent Hub, central Agent Control, claims ledger or mandatory external skills.
- Do not scan all docs, issues, branches, worktrees or history, and do not delegate unless requested. For an explicitly requested inventory/audit, inspect the relevant wider scope read-only.

## Decision-first development

- The owner decides **what** Jularr does and **how** its UX behaves. Before implementing new features or changing behavior, UI, data contracts, architecture or scope: inspect the relevant current code, existing issues/PRs and prior agreed decisions; identify what is working, incomplete or missing.
- Interview the owner about meaningful unresolved requirements, UX, edge cases, compatibility and acceptance criteria. Ask concrete questions; do not invent preferences, silently settle product decisions or re-ask decisions already made. If a clear, narrowly scoped fix has already been explicitly approved, do not create a redundant interview.
- Before implementation, establish one approved issue/specification or an equivalently explicit approved task: goal, current vs. desired behavior, UX where relevant, technical boundaries, dependencies, failure cases, acceptance criteria, focused tests and exclusions. Obtain explicit owner approval for the scope. A plan, exploratory request, mockup or prior general permission alone is not implementation approval.
- Work on one approved, testable vertical slice at a time. Finish its code, focused checks, relevant end-to-end/integration checks and reported verification before beginning another feature. Mark work **Done** only after owner acceptance; a passing build or open PR is not product acceptance.
- Do not expand scope, add speculative features, perform unrelated refactors or build multiple overlapping implementations. If a material decision, failed permission/automated review or unsafe migration blocks the slice, stop that affected work, report the exact blocker and ask; do not repeatedly retry rejected edits or bypass controls. Independent work can resume only if it was separately approved.
- Optimize effort: targeted file reads and tests, reuse existing context and owners, avoid repeated repository-wide scans, stop unproductive loops, keep PRs small. Never claim a feature is finished without evidence.
- For audit, interview or planning requests, perform no code changes unless explicitly authorized. For issue/PR/branch cleanup, classify and recommend first; never close, delete, retarget, merge or discard existing work without explicit item-specific authorization.

## Legacy feature preservation

- Development is driven only by owner-approved features, GitHub issues and acceptance criteria. Historical implementation schedules and milestone labels do not authorize or block work.
- Before retiring any old plan or deleting/closing its associated issue, PR, branch or document, inventory the relevant functionality and approved decisions from the previous Jularr code (including `dev`/`main`) and historic specifications; compare each capability with `main`. Record a verifiable mapping: implemented and tested on `main`, or missing/partial with an open, linked GitHub issue. Preserve prior decisions and dependencies, and get owner confirmation where intent is ambiguous.
- Do not declare legacy feature preservation complete until the audit identifies no untracked functionality or agreed feature. Record missing items as issues without automatically implementing them; owner separately prioritizes and approves implementation.

## Scope and safety

- Extend the canonical owner; do not add parallel business rules or unnecessary abstractions.
- Preserve unrelated and uncommitted changes. Do not reset, delete or overwrite user work without explicit authorization.
- Never expose or commit secrets. Do not change an active database; use isolated databases for tests.
- The database baseline `database/*.sql` is frozen (schema version 1, `database/baseline.sha256`; CI fails on edits). Schema changes are new numbered migrations `database/migrations/NNNN_short_name.sql`: [Database migrations](docs/DATABASE_MIGRATIONS.md).
- Discuss framework limitations and new dependencies before adding workarounds. Frontend owns the single web host and invokes Services only. Do not modify active databases or run destructive migrations without an individually approved migration plan, verified backups, rollback and isolated tests.

## Validation and Git

- Run only the affected build and necessary focused tests. Documentation-only changes need link/path and diff checks, not backend tests.
- Full validation commands are in [.agent/project.yaml](.agent/project.yaml); run them only for a release or an explicit full-validation request.
- Review your own diff before committing; state what was verified and any remaining integration gaps.
- Start each approved package on its own feature branch from `origin/main`, use one bundled commit and a separate PR, and push after focused verification. Do not merge automatically: each PR requires green checks, documented verification and explicit owner approval to merge. [BACKEND_NEXT](docs/BACKEND_NEXT.md) is reference material, not blanket scope approval. Preserve existing work, never force push, and never merge into `dev`.
- Both author and committer must be exactly `Juloc <juli.hiresch@gmail.com>`; no AI attribution or additional contributor trailers. The only other accepted committer is GitHub itself (`GitHub <noreply@github.com>`) on squash and merge commits made on github.com; their author must still be Juloc.
- Feature push checks exclude already merged `origin/main` ancestors; newly contributed commits and target-branch pushes remain fully checked. Never rewrite historical authorship.
- Before committing, run `node scripts/install-git-hooks.mjs`. Never bypass hooks with `--no-verify`.
- Keep commit messages compact. Do not rewrite historical authorship.

## Documentation

- Keep this file a short entry point, conventions and UX decisions in their linked owners, and CODEMAPs as navigation only.
- Update affected documentation with the implementation; do not duplicate rules or retain obsolete paths. Historical references must not assert current implementation status.
- In handoffs, report the outcome, focused verification and open work briefly; no large planning documents unless requested.
