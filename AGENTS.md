# Jularr agent instructions

## Start here

- Follow the current user request; explicit owner decisions override stale plans and legacy behavior.
- Read [conventions](docs/CONVENTIONS.md) once per continuous session before implementation.
- For Frontend work, also read [Frontend conventions](docs/FRONTEND_CONVENTIONS.md) and [Frontend CODEMAP](src/Jularr.Frontend/CODEMAP.md).
- Use [CODEMAP](docs/CODEMAP.md) to find the relevant owner and focused tests. Read only the needed files and linked specifications; use targeted `rg` searches.
- These local documents are authoritative. No Agent Hub, central Agent Control, claims ledger or mandatory external skills.
- Do not scan all docs, issues, branches, worktrees or history, and do not delegate unless requested.

## Scope and safety

- Extend the canonical owner; do not add parallel business rules or unnecessary abstractions.
- Preserve unrelated and uncommitted changes. Do not reset, delete or overwrite user work without explicit authorization.
- Never expose or commit secrets. Do not change an active database; use isolated databases for tests.
- Ask only when a missing decision or permission materially changes scope or safety.
- Current scope includes the clean-start Backend and Frontend. Propose each area's Services, tables, Logic and rough Frontend to the owner before implementation. Deliver approved work in small verified bundles, one commit and push per topic; agree the next topic before starting it. Frontend owns the single web host and invokes Services only. Phase B remains stopped; no clean-cut migrations or active-database changes.

## Validation and Git

- Run only the affected build and necessary focused tests. Documentation-only changes need link/path and diff checks, not backend tests.
- Full validation commands are in [.agent/project.yaml](.agent/project.yaml); run them only for a release or an explicit full-validation request.
- Review your own diff before committing; state what was verified and any remaining integration gaps.
- Current branch: `codex/media-management-foundation`. Confirmed role/storage/standalone-Arr/player scope is in [BACKEND_NEXT](docs/BACKEND_NEXT.md). Keep changes bundled and push after focused verification. The owner authorized clean-start integration and continued Backend/Frontend implementation; preserve existing work, never force push, and never merge into `dev`/`main`.
- Both author and committer must be exactly `Juloc <juli.hiresch@gmail.com>`; no AI attribution or additional contributor trailers.
- Feature push checks exclude already merged `origin/clean-start` ancestors; newly contributed commits and target-branch pushes remain fully checked. Never rewrite historical authorship.
- Before committing, run `node scripts/install-git-hooks.mjs`. Never bypass hooks with `--no-verify`.
- Keep commit messages compact. Do not rewrite historical authorship.

## Documentation

- Keep this file a short entry point, conventions in one linked document and CODEMAPs as navigation only.
- Update affected documentation with the implementation; do not duplicate rules or retain obsolete paths.
- In handoffs, report the outcome, focused verification and open work briefly; no large planning documents unless requested.
