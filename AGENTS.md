# Jularr agent instructions

Jularr is a self-hosted media platform built by AI agents working in parallel. These are the working rules; details live in the linked documents.

## Read first

- [Documentation index](docs/INDEX.md): one line per document.
- [CODEMAP](docs/CODEMAP.md): one line per owner area. Open the area file of your task (`docs/areas/<area>.md`) and only the code it names.
- Backend work: the relevant section of [Conventions](docs/CONVENTIONS.md). Frontend work: the relevant sections of
  [Frontend conventions](docs/FRONTEND_CONVENTIONS.md) and [Frontend UX](docs/FRONTEND_UX.md).
- Use targeted `rg` searches and reads; do not scan all docs, issues, branches or history. `docs/archive/` is history, excluded from search.
- Do not start sub-agents unless asked.

## Decisions

- The owner decides what Jularr does and how its UX behaves. Recorded decisions stay until the owner changes them.
- Fix bugs and do clearly scoped work directly. Ask the owner short, concrete questions only when a product or UX decision is open, the scope is
  unclear, or the change would alter recorded behaviour. Never invent preferences.
- Audit, planning and interview requests change no code unless the owner says so.
- Stay in scope: no speculative features, unrelated refactors or parallel implementations. Extend the existing owner.
- Ask before adding a dependency or building a workaround for a framework limit.
- Never close, delete, retarget, merge or discard issues, PRs, branches or other work without the owner's explicit go for that item.
  Removing a feature needs the owner's yes; record anything approved but missing as an issue.

## Safety

- Never expose or commit secrets.
- Never change an active (live) database. Tests use isolated databases.
- The database baseline `database/*.sql` is frozen; schema changes are new numbered migrations ([Database migrations](docs/DATABASE_MIGRATIONS.md)).
  A destructive migration needs an owner-approved plan with backup, rollback and an isolated test.
- Preserve unrelated and uncommitted changes; never reset, delete or overwrite other work without authorization.
- If a permission, hook or review blocks you, stop that work and report the blocker; do not retry blindly or bypass it.

## Validation

- Run the affected build and focused tests only. Full validation ([project config](.agent/project.yaml)) is for releases or when asked.
- Page and flow changes are also checked in a real browser (`tests/Jularr.E2E.Tests`).
- Review your own diff before committing. Report what you verified and what you did not; never claim a feature or check is done without evidence.

## Git and PRs

- One feature branch from `origin/main` per change, one bundled commit, one PR. Never force push, never merge into `dev`.
- Merge only with green checks and the owner's explicit yes.
- Author and committer are exactly `Juloc <juli.hiresch@gmail.com>`, with no AI attribution or co-author trailers.
  The only other committer is GitHub (`GitHub <noreply@github.com>`) on squash and merge commits made on github.com; their author is still Juloc.
  Never rewrite historical authorship.
- Run `node scripts/install-git-hooks.mjs` before committing; never bypass hooks with `--no-verify`.
- Feature push checks skip commits already on `origin/main`; new commits and pushes to target branches are fully checked.

## Documentation

- Update the area file of the feature in the same change; new behaviour goes there, never into a CODEMAP. Delete what became wrong.
- Where things belong and the size limits: [Documentation policy](docs/DOCUMENTATION_POLICY.md); check with `node scripts/verify-documentation.mjs`.
- Handoffs are short: outcome, verification, open work.
