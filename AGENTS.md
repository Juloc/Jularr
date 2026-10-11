# Jularr agent instructions

## Start here

- Follow the current user request; explicit owner decisions override stale plans and legacy behavior.
- Read [conventions](docs/CONVENTIONS.md) once per continuous session before implementation.
- For Frontend work, also read [Frontend conventions](docs/FRONTEND_CONVENTIONS.md); use its [Frontend CODEMAP](src/Jularr.Frontend/CODEMAP.md) for UI ownership.
- Use [CODEMAP](docs/CODEMAP.md) to find the relevant owner and focused tests. Read only the needed files and linked specifications; use targeted `rg` searches.
- These local documents are authoritative. No Agent Hub, central Agent Control, claims ledger or mandatory external skills.
- Do not scan all docs, issues, branches, worktrees or history, and do not delegate unless requested.

## Scope and safety

- Extend the canonical owner; do not add parallel business rules or unnecessary abstractions.
- Preserve unrelated and uncommitted changes. Do not reset, delete or overwrite user work without explicit authorization.
- Never expose or commit secrets. Do not change an active database; use isolated databases for tests.
- Ask only when a missing decision or permission materially changes scope or safety.
- Canonical architecture: Backend class library with Services/Logic/Data and PostgreSQL; Frontend owns the one ASP.NET Core Razor/API host. Frontend UI text definitions synchronize through the existing Backend PostgreSQL localization owner. Coordinate simultaneous changes to shared contracts; do not introduce clean-cut migrations without explicit authorization.

## Validation and Git

- Run only the affected build and necessary focused tests. Documentation-only changes need link/path and diff checks, not backend tests.
- Full validation commands are in [.agent/project.yaml](.agent/project.yaml); run them only for a release or an explicit full-validation request.
- Review your own diff before committing; state what was verified and any remaining integration gaps.
- Use the current agreed task branch and refresh its base before publishing. Preserve concurrent work, do not merge into `clean-start`, `dev` or `main` without explicit authorization, and never force-push.
- Both author and committer must be exactly `Juloc <juli.hiresch@gmail.com>`; no AI attribution or additional contributor trailers.
- Before committing, run `node scripts/install-git-hooks.mjs`. Never bypass hooks with `--no-verify`.
- Keep commit messages compact. Do not rewrite historical authorship.

## Documentation

- Keep this file a short entry point, conventions in one linked document and CODEMAPs as navigation only.
- Update affected documentation with the implementation; do not duplicate rules or retain obsolete paths.
- In handoffs, report the outcome, focused verification and open work briefly; no large planning documents unless requested.
