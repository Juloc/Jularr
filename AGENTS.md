# Juloc Agent Bootstrap

Do not ask the user to repeat the Agent Control protocol, test budget, context rules or visual rules in their prompt; they live in the skills below.

## Skill routing

| Work | Skills |
| --- | --- |
| Normal implementation, bug, issue | `jularr-task` |
| UI, Razor, CSS, visual component | `jularr-task` + `jularr-ui` |
| Acquisition, Wanted, Arr, Search, Selection, Import | `jularr-task` + `jularr-acquisition` |
| Schema, EF, PostgreSQL | add `jularr-db-change` |
| Playback, player, streaming | add `jularr-playback-work` |
| Unattended ordered run | `jularr-task` + specialist skill + `jularr-overnight` |
| Before a substantial commit | `jularr-review` (own diff, no subagent by default) |

## Startup cost

Once per continuous session, before the first substantial work:

1. Read `.agent/project.yaml` for repository-specific commands, protected paths and related repositories.
2. Read the canonical operating rules from `Juloc/agent-control/AGENTS.md` on `main` using the available GitHub access, plus the engineering and maintainability documents it names.
3. Read `docs/MAINTAINABILITY_CONVENTIONS.md`.
4. Resolve the active coordination ledger through `Juloc/agent-control` issue #1 and claim the smallest necessary scope according to the central protocol.
5. Read any additional repository-specific instruction file named in `.agent/project.yaml` notes.

Per slice, read only: the current request, the directly relevant issue, the binding SPEC, the canonical owner and its directly related tests. Do not reread unchanged central rules or inventory unrelated branches, PRs, worktrees or ledger history for every slice. Re-check coordination when the scope changes, `origin/dev` moved unexpectedly, concurrent work appears, or before push. The ordinary test budget is focused tests only; the `validation` list in `.agent/project.yaml` is the release gate or explicit-request validation, not the per-slice completion step (see `jularr-task`).

## Mandatory AI implementation discipline

The central `Juloc/agent-control` engineering and C# conventions are binding for every agent. The following project rule makes the required application style explicit and does not weaken the central baseline:

- Apply current Microsoft/.NET naming and layout conventions to every new or intentionally touched C# member: PascalCase/camelCase naming, four spaces and Allman braces.
- Apply the central and local maintainability conventions to every implementation task. They may not be skipped because code is AI-generated, because nearby legacy code is worse, or because the requested change is small.
- Comments must explain non-obvious intent, invariants, constraints or trade-offs. Do not add comments that merely narrate the method name or obvious code. Use useful XML documentation for public/reusable contracts when callers need it.
- Do not split coherent control flow into artificial mini-methods. Introduce a helper only when it owns real domain behavior, validation/policy, complexity, reuse or an architectural/framework boundary. Never add a forwarding wrapper that merely calls another method unchanged.
- Before adding a service, store, helper, interface, factory, provider, manager, handler, configuration path or dependency, inspect the current Jularr owner/pattern first and justify the new structure with a current concrete need.
- Keep method calls, constructors/object creation, conditions, signatures and LINQ pipelines on one physical line whenever the resulting line is at most 230 characters and remains readable. Do not turn ordinary calls into vertically fragmented argument lists. Lines over 280 characters are not allowed; between 231 and 280, prefer the clearest structure and restructure before fragmenting ordinary control flow.
- When a structural rule is deterministic and safely machine-checkable, add or extend an architecture/source/CI guard instead of relying on prose alone.
- Run the maintainability completion gate and review the edited C# area against all applicable conventions before declaring work complete. Generated, vendored and tool-owned code is exempt only where manual edits would be overwritten.

## Mandatory maintainability enforcement

- Every changed responsibility must still have one clear canonical owner.
- Do not create parallel data, configuration, business-rule or feature paths.
- Reads and validation must not hide unrelated mutations.
- Errors must preserve meaning; do not swallow failures or silently fall back to legacy/default behavior.
- Propagate cancellation and use real timeouts for I/O-bound work.
- Avoid N+1 database access, loading full datasets for application-side filtering, unnecessary mapping/allocation layers and speculative hot-path abstraction.
- Behavior/regression tests are preferred over tests of private wiring.
- Do not leave obsolete compatibility branches, commented-out code or vague TODO debris behind.
- A new maintainability violation in intentionally touched code means the task is not complete.

## Authorship enforcement

- Commits must use the configured real contributor identity. AI, agent and service identities or attribution trailers are prohibited.
- Run `npm ci` before committing to activate the repository-local authorship hooks. Do not bypass them with `--no-verify`.
- Never force push and never modify `main` for ordinary work; ordinary work lands on `dev`.
- CI verifies authorship again for every pull request and push; the protected `main` branch requires that verification to pass.

## Fallback if the central repository cannot be read

Continue safely without blocking repository recovery:
- inspect this repository's open Issues and PRs before editing,
- avoid work that overlaps an active branch/PR,
- use GitHub Issues as the backlog instead of creating manual status/backlog Markdown,
- keep changes scoped and run focused validation for the changed area (`jularr-task` test policy),
- never put credentials or secrets in code, docs, issues or comments,
- record that cross-agent coordination could not be verified.

The Agent Hub is optional. GitHub remains the durable source of truth.
