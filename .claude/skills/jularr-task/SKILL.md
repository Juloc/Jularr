---
name: jularr-task
description: Default Jularr workflow for normal implementation slices. Uses the compact Agent Control runtime policy; specialist skills add only task-specific rules.
---

# Jularr task

Use the compact central runtime policy and `AGENTS.md` as the generic workflow. Do not reload the large central rule documents unless the task reaches a detail that is not covered by the compact policy.

## Session setup

Once per continuous session:

- read `AGENTS.md` and `.agent/project.yaml`;
- load Agent Hub `/v1/context?repository=Juloc/Jularr` when available, otherwise read `Juloc/agent-control/hub/runtime-policy.json`;
- claim the smallest necessary scope;
- work from current `origin/dev` and push ordinary work to `dev`; never touch `main`, publish a release or force push unless the user explicitly requested that workflow.

Use `jularr-dev-env` only when local environment/Docker/site operation is actually relevant.

## Understand the slice

Start from the current user request.

1. Read a concise feature `CODEMAP.md` first when one exists.
2. Use targeted search to find the canonical owner, direct caller/persistence path and directly related tests.
3. Prefer an initial working set of about 12 or fewer directly relevant source/test files.
4. Read an issue/spec/mockup/ADR only when the current task explicitly depends on it. Do not automatically load planning material.
5. Stop broad searching once the owner and required path are known. Widen only for a named dependency, contradiction or blocker.

Current explicit owner/user product direction is highest product authority. Do not let stale specs/issues or existing code silently reintroduce an old product concept. If a needed product decision is genuinely undefined, stop that affected part and report it instead of inventing policy.

## Implement

Extend the canonical owner and make the smallest coherent root-cause change.

- One responsibility, one owner/source of truth.
- No second store, state machine, scheduler, pipeline, config path or compatibility fallback for convenience.
- Apply the central flat-comment rule: ordinary code/tests have no comment; short `// why` only when non-obvious; XML docs only where callers genuinely need them.
- When replacing Jularr-owned behavior, remove the superseded runtime path plus dead tests/config/UI/docs in the same coherent change.
- Use the specialist skill only for the concern actually touched: `jularr-ui`, `jularr-acquisition`, `jularr-db-change`, `jularr-playback-work`.
- Do not do adjacent cleanup that is unrelated to the changed responsibility.

## Focused validation

Tests are required, but routine broad validation is not.

- Target 1-5 directly relevant test classes and normally fewer than about 100 tests.
- Prefer one focused regression/invariant test over many near-identical permutations; use data-driven cases for equivalent variants.
- Do not run `dotnet test Jularr.sln` for ordinary slice completion. The full suite is for release gates, explicit user requests or explicitly requested full validation.
- Build the smallest meaningful target. Do not restore/build the entire solution repeatedly without a concrete need.
- Confirm the real exit status. Do not pipe away the producer exit code or chain test/build with commit/push.
- Do not rerun an identical green command without a relevant source change.
- A directly blocking failure must be understood; `pre-existing` alone is not an exemption.

## Finish

1. Review only the final diff and directly affected owner with `jularr-review`; do not turn review into a repo audit.
2. Re-check `origin/dev` and coordination before push. If `dev` moved, reconcile without discarding another agent's changes.
3. Remove scratch/debug artifacts created by this task.
4. Commit with the configured real identity and a compact message; no agent attribution and no hook bypass.
5. Push to `dev`.
6. Update the task issue/PR only when the work is actually issue/PR-backed.
7. Post `done`/`release` for the claim and stop. Only `jularr-overnight` overrides the one-slice stop rule.

## Context/output budget

Use one lead agent. A subagent is exceptional and must save more context/work than it costs. Do not spawn agents to search files, reread docs, run tests, wait or perform routine self-review.

Do not narrate routine grep/read/edit/test/git commands. Report only a real decision, blocker, completed slice and concise final state.
