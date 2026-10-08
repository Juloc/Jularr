# Juloc Agent Bootstrap

Do not ask the user to repeat Agent Control, test, context or visual rules in task prompts.

## Skill routing

| Work | Skills |
| --- | --- |
| Normal implementation, bug, issue | `jularr-task` |
| UI, Razor, CSS, visual component | `jularr-task` + `jularr-ui` |
| Acquisition, Wanted, Arr, Search, Selection, Import | `jularr-task` + `jularr-acquisition` |
| Schema, EF, PostgreSQL | add `jularr-db-change` |
| Playback, player, streaming | add `jularr-playback-work` |
| Dev environment, Docker, local site, `dotnet watch` | `jularr-dev-env` |
| Unattended ordered run | `jularr-task` + specialist skill + `jularr-overnight` |
| Before a substantial commit | `jularr-review` (own diff, no subagent by default) |

## Fast startup

Once per continuous session:

1. Read `.agent/project.yaml`.
2. Load the compact central runtime policy. Prefer Agent Hub `GET /v1/context?repository=Juloc/Jularr` and its `runtimePolicy`; without Hub access read `Juloc/agent-control/hub/runtime-policy.json` on `main`.
3. Resolve coordination through Agent Hub when configured, otherwise through `Juloc/agent-control#1`, and claim the smallest necessary scope.
4. Load the specialist skill for the current task.

The large central engineering/maintainability/C#/database documents and this repository's detailed maintainability document are reference material. Do not reread them in full by default. Read only the section a concrete task reaches.

Per task:

- start from the current user request;
- read a concise feature `CODEMAP.md` first when one exists;
- use targeted symbol/path search to find the canonical owner and focused tests;
- read a linked issue/spec/mockup/ADR only when the current task actually depends on it;
- stop broad discovery once the owner, entry point, persistence path and focused tests are known.

Do not automatically read all issues, specs, mockups, docs, branches, PRs, worktrees or git history.

Current explicit owner/user product decisions are the highest product authority. They override stale planning documents, old issue assumptions, old specs and existing implementation behavior. If a required product decision is genuinely undefined, stop that affected part instead of inventing a compromise.

## Jularr implementation refinements

The central compact runtime policy is binding. Jularr additionally requires:

- Keep the modular monolith and one canonical owner per responsibility. No parallel state/config/business-rule path.
- Extend the current owner before adding a service, store, helper, interface, factory, provider, manager, handler, configuration path or dependency.
- New or intentionally touched C# follows current Microsoft/.NET naming/layout, four spaces, Allman braces and the repository line-width rules.
- Default to **no source comment**. Use one short `//` comment only for a non-obvious invariant, constraint, trade-off or external quirk. Multi-line prose is exceptional.
- XML documentation is only for reusable/public contracts whose callers need non-obvious information. Ordinary application types, enums, constants, methods and tests normally have no XML summaries.
- Do not put issue numbers, spec prose, change history or obvious behavior narration in source comments.
- Keep coherent control flow together; no forwarding wrappers or artificial mini-methods.
- Propagate cancellation and real timeouts for I/O; preserve error meaning.
- Avoid N+1 access, whole-dataset application filtering, repeated parsing/I/O and per-row save loops for bulk work.
- When a new canonical path replaces an old Jularr-owned path, migrate required live state once and remove the superseded code, tests, config, UI and stale docs. Do not keep `Legacy*`, `V1*`, fallback or compatibility paths without a concrete supported requirement.
- Tests protect behavior/regressions. Prefer concise `Subject_Condition_Result` names, data-driven cases for equivalent variants, no test XML prose and no redundant permutations.
- The `validation` list in `.agent/project.yaml` is the release/explicit-full-validation gate, not ordinary slice completion.

Feature CODEMAPs are navigation indexes only. Create one only when it materially reduces future discovery, normally 30-60 lines with purpose, owner/entry points, persistence, important callers and focused tests. Do not create one per folder or duplicate specs/architecture prose.

## Authorship and branch workflow

- Ordinary work lands directly on `dev`; never modify `main` for normal work and never force push.
- Commits use the configured real contributor identity; no AI/agent/service attribution.
- Run the repository-required hook/bootstrap step before committing and never bypass hooks with `--no-verify`.
- Commit messages stay compact: one clear subject and only a few short bullets when non-obvious consequences need explanation.
- CI verifies authorship again for pushes/PRs.

## Coordination and fallback

Re-check coordination when scope changes, `origin/dev` moves unexpectedly, concurrent work appears, or immediately before push. Do not reread the ledger repeatedly during an unchanged slice.

If Agent Hub is unavailable, use the same compact policy and coordination ledger directly through GitHub. If central Agent Control cannot be read, continue from local instructions without broadening scope, preserve security/authorship/secret rules, avoid known overlap and record that central policy/coordination could not be refreshed.

GitHub remains the durable source of truth.
