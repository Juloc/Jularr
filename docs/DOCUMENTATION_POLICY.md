# Documentation authority

Status: **active**. This page identifies the owner of each kind of information; it does not approve any implementation.

| Question | Primary authority |
| --- | --- |
| What currently works? | `main` implementation and focused tests; an unmerged PR is not deployed behavior |
| What should work? | Latest explicit Owner-approved feature decision, specification and acceptance criteria |
| Where is it implemented? | [CODEMAP](CODEMAP.md), then the current owning source and tests |
| How is code structured? | [AGENTS](../AGENTS.md), [Backend conventions](CONVENTIONS.md) and [Frontend conventions](FRONTEND_CONVENTIONS.md) |
| What is the shared UI behavior? | [Frontend UX decisions](FRONTEND_UX.md), subject to newer explicit feature decisions |
| Where is open work? | GitHub issues and PRs, not a dated rollout plan |

Implementation reality and approved desired behavior are different. An explicit later Owner decision may supersede an older design but does not make unmerged code available on `main`. Record both states and link the issue or PR if they differ.

## Lifecycle

- **Active** rules/specs govern new changes within their scope and must be updated when the corresponding behavior changes.
- **Historical** plans and legacy inventories are preserved as recovery evidence only. They grant no implementation permission or completion claim.
- **Snapshot** test matrices are valid only for the date/revision they describe. Do not repeat their cells as current `main` status.

[Alpha-90 recovery](ALPHA90_RECOVERY_PLAN.md), [backend slices](BACKEND_NEXT.md), [completion packages](COMPLETION_PLAN.md) and the [feature matrix](FEATURE_MATRIX.md) are historical/snapshot references. They are not the current backlog.

Keep AGENTS short, CODEMAPs as owner/file/test navigation, README as present-tense usage information, and behavior requirements in one canonical feature spec. Before removing an old plan or legacy implementation, map each approved capability to tested `main` behavior or an open linked issue, as required by AGENTS. Do not delete untracked requirements.

Run `node scripts/verify-documentation.mjs` after changes to active documentation. The checker validates links and known stale-status assertions, but cannot replace code/spec review.
