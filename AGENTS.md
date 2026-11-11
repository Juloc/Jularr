# Jularr agent rules

Jularr is a self-hosted media platform. Build, test, migration and release commands and the few code and UX rules: [Development](docs/DEVELOPMENT.md).
Find code with `rg`; the code is the reference.

## Owner

- Open product and UX questions are the owner's decision: ask briefly instead of guessing. Bugs and clearly scoped work need no question.
- No sub-agents unless asked.

## Safety

- Never commit or show secrets.
- Never touch a live or dev database. Tests run only against the isolated test database.
- The baseline `database/*.sql` is frozen. Schema changes are new `database/migrations/NNNN_name.sql` files.
- Do not overwrite other people's or uncommitted changes.

## Git

- One branch from `origin/main`, one PR.
- Author and committer are exactly `Juloc <juli.hiresch@gmail.com>`, with no AI attribution or co-author lines.
  GitHub's own squash and merge commits are the only other committer.
- Run `node scripts/install-git-hooks.mjs` before committing. Never use `--no-verify`, never force push, never merge into `dev`.
- Merge only with green checks and the owner's yes.

## Checks

- Run only the affected builds and tests.
