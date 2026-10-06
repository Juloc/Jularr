---
name: jularr-reconcile-existing-work
description: Inventory and safely reuse existing Jularr work in open PRs, local/remote branches, worktrees and uncommitted diffs before building anything new. Use before implementing a slice and when cleaning up after concurrent agents.
---

# Jularr reconcile-existing-work

Never delete, reset, clean, force-push or switch away from uncommitted work. A stale claim does not make Git data disposable.

1. **PRs**: `gh pr list --repo Juloc/Jularr --state open --json number,title,headRefName,baseRefName,isDraft,updatedAt`. For each relevant PR read its diff, linked issue and mergeability.
2. **Branches**: `main` history was rewritten on 2026-09-29, so old pre-rewrite branches share no commits with `dev` and plain ahead/behind counts mislead. Use `git cherry origin/dev <branch> | grep -c '^+'` (patch-id based) to count genuinely unique patches. Classify each branch: integrated, superseded, partially useful, obsolete, Games/Learning (deferred) or relevant core media.
3. **Worktrees**: `git worktree list`; per worktree `git status --porcelain`, `git cherry origin/dev HEAD` and `git stash list`. For uncommitted work inspect the diff, find its issue/spec, preserve it (leave it in place or export a patch outside the repo) and port only what is still correct to the current architecture through the normal path.
4. **Integrate safely** by porting a small diff, cherry-picking an isolated valid commit, or re-implementing against current code. Never blind-merge a stale branch; bring migrations/snapshots up to the current schema; remove duplicates.
5. **Disposition** every source explicitly: integrated, superseded, deferred (out of scope), left because a fresh live owner took it, or obsolete. Comment on or close stale PRs whose work landed or was superseded, with the reason.
