# Documentation policy

Status: **active**. Where information belongs and how large documents may get. `node scripts/verify-documentation.mjs` checks the limits in CI.

## Where things belong

| Question | Owner |
| --- | --- |
| What works now? | `main` code and its tests; an unmerged PR is not deployed behaviour |
| What should work? | The owner's latest explicit decision: an area file's Decisions, [Frontend UX](FRONTEND_UX.md), a spec or the issue |
| Where is it implemented? | [CODEMAP](CODEMAP.md), then `docs/areas/<area>.md`, then the code it names |
| How is code written? | [Conventions](CONVENTIONS.md) and [Frontend conventions](FRONTEND_CONVENTIONS.md) |
| Where is open work? | GitHub issues and PRs |

- `AGENTS.md`: working rules only. [INDEX](INDEX.md): one line per document.
- CODEMAPs: one line per area (code paths, focused tests, link); never behaviour prose.
- Area files (`docs/areas/<area>.md`): the area's decisions and behaviour in Decisions, Backend, Frontend and Tests sections.
  Update them in the same change as the code. A new area gets a CODEMAP line.
- Specs (`docs/*.md`): only for approved larger features whose behaviour the code does not show; name their status near the top.
- `README.md` describes startup and current capabilities; it is not a rules document.
- History (old plans, snapshots, migration traces) lives in `docs/archive/` with `Status: **historical**` or `**snapshot**`.
  It grants no permission and claims no current state; `.rgignore` keeps it out of searches.
- When behaviour changes, change its one owner and delete what became wrong; Git keeps the history.
  Rules carry no dates or issue numbers.

## Limits

- `AGENTS.md` at most 120 lines, [INDEX](INDEX.md) at most 60 lines, each CODEMAP at most 80 lines.
- Area files at most 250 lines and 24 KB; split an area that grows past that and add its CODEMAP line.
- Other active documents at most 40 KB (exceptions with their own cap are listed in the checker).
- Lines at most 160 characters in AGENTS, INDEX, both CODEMAPs, area files, the conventions, Frontend UX and this file (table rows excepted).
  Write one sentence per line; continuation lines of a bullet are indented by two spaces.
- Local links and code paths (`src/…`, `tests/…`, `database/…`, `scripts/…`, `docs/…`, `Jularr.<Project>/…`) must exist;
  in CODEMAPs every path in backticks must exist.
- No near-duplicate sentences across active documents: write it once and link to it.
