# Jularr agent instructions

## Start here

- Follow the current user request; explicit owner decisions override stale plans and legacy behavior.
- Read [conventions](docs/CONVENTIONS.md) once per continuous session before implementation.
- Use [CODEMAP](docs/CODEMAP.md) to find the relevant owner and focused tests. Read only the needed files and linked specifications; use targeted `rg` searches.
- These local documents are authoritative. No Agent Hub, central Agent Control, claims ledger or mandatory external skills.
- Do not scan all docs, issues, branches, worktrees or history, and do not delegate unless requested.

## Scope and safety

- Extend the canonical owner; do not add parallel business rules or unnecessary abstractions.
- Preserve unrelated and uncommitted changes. Do not reset, delete or overwrite user work without explicit authorization.
- Never expose or commit secrets. Do not change an active database; use isolated databases for tests.
- Ask only when a missing decision or permission materially changes scope or safety.
- Current scope includes backend/auth/Profile/localization and Integrations, SQL binding/paging cleanup, Frontend inclusion in the solution, the local clean-start preview on D: and instance wizard consolidation. The Frontend owns the single web host; only its two Data imports were changed with approval. Other Frontend source changes remain out of scope. Wizard/Admin/Library/Home/Player/Arr+ implementation and NAS scope still need decisions. Phase B remains stopped; no clean-cut migrations.

## Validation and Git

- Run only the affected build and necessary focused tests. Documentation-only changes need link/path and diff checks, not backend tests.
- Full validation commands are in [.agent/project.yaml](.agent/project.yaml); run them only for a release or an explicit full-validation request.
- Review your own diff before committing; state what was verified and any remaining integration gaps.
- Current branch: `codex/local-clean-start-server`, based on `clean-start` after the authorized Integrations/formatting merge. Keep changes bundled and push the feature branch after focused verification. Preserve Frontend work; further merges into `clean-start` or writes to `dev`/`main` require explicit authorization. Never force push.
- Both author and committer must be exactly `Juloc <juli.hiresch@gmail.com>`; no AI attribution or additional contributor trailers.
- Before committing, run `node scripts/install-git-hooks.mjs`. Never bypass hooks with `--no-verify`.
- Keep commit messages compact. Do not rewrite historical authorship.

## Documentation

- Keep this file a short entry point, conventions in one linked document and CODEMAPs as navigation only.
- Update affected documentation with the implementation; do not duplicate rules or retain obsolete paths.
- In handoffs, report the outcome, focused verification and open work briefly; no large planning documents unless requested.
