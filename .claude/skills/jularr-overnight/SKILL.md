---
name: jularr-overnight
description: Autonomous unattended multi-slice Jularr run through an explicit ordered objective list. Use ONLY when the user asks for overnight/unattended/"while I am away" work. Extends jularr-task and overrides its stop rule.
---

# Jularr overnight

Use only on an explicit request for overnight, unattended, continue-while-away or ordered-objective work. Extends `jularr-task` (authority, owner first, test policy, build and non-interactive rules all apply) plus the relevant specialist skill. Only the stop rule differs: do not stop after one slice and do not ask the user to continue; work through the ordered objectives.

## Loop per coherent slice

1. Implement the slice completely (`jularr-task`); inspect your diff (`jularr-review` checklist).
2. Focused validation only.
3. Commit with the real contributor identity, no agent attribution.
4. Re-check coordination (ledger, `origin/dev` moved?) before push, rebase if needed, push to `dev` safely, never force.
5. Continue with the next objective automatically. Keep the claim's lease alive; extend or re-claim when the scope changes.

## Blockers

A blocker in one objective does not end the run: identify the exact blocker, leave the repository clean (no half-written slice), record it for the final report, continue with the next independent objective, and return later if another slice resolves it. Normal implementation uncertainty is not a blocker; decide using the authority order in `jularr-task`. No routine questions. Defer only when two genuinely different product behaviors are equally consistent with every binding source: do not invent behavior, record the product blocker and continue independent work.

## Safety and cost

No firewall/security prompts, interactive authentication or package-install prompts (cancel, do not wait, use a local non-interactive alternative, record it). No full-suite validation, agent waves, background test jobs, broad audits or "while I am here" backlog exploration. Do not leave a deliberately half-written slice: if context, time or resources run low, finish the current coherent slice, commit and push cleanly, release the claim, then stop.

## Reporting

No long intermediate retrospectives. One final user-facing report: what landed (commit SHAs), blockers (exact), limitations (including that the full suite was not run), and what remains.
