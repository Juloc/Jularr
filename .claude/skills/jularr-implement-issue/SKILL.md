---
name: jularr-implement-issue
description: Implement one Jularr GitHub issue end to end - acceptance criteria, canonical owner, tests, legacy removal, validation, commit to dev, truthful issue update, claim release. Use after jularr-start-task.
---

# Jularr implement-issue

1. Extract the acceptance criteria into a checklist from the full issue body and comments. Precedence: architecture/domain ownership > binding `docs/**` specs > issue acceptance > later documented decisions > approved mockups > existing code > old branches.
2. Locate the canonical owner and existing pattern and extend it. Do not add a parallel service, scheduler, progress model, table or config store. One business rule has one backend owner; the UI mirrors state only.
3. Claim the scope (see `jularr-start-task`).
4. Implement the smallest complete slice. C# rules: Microsoft naming, Allman braces, four spaces, calls/signatures/LINQ on one line when at most 230 characters (hard maximum 280), no forwarding wrappers or mini-method fragmentation, comments only for non-obvious intent, cancellation tokens and timeouts on I/O, no N+1, no swallowed exceptions, no hidden mutation in reads, DTOs rather than entities across layers.
5. Persistence changes: `jularr-db-change`. UI changes: `jularr-ui-from-spec`. Playback: `jularr-playback-work`.
6. Tests: behavior and regression tests (idempotent retry, restart recovery, authorization, state transitions). PostgreSQL behavior is tested on real PostgreSQL. Add or extend a source/architecture guard when the invariant is objectively checkable.
7. Replace, do not layer: when a new canonical path replaces an old one, migrate data once, switch reads and writes, then delete the obsolete runtime path, its tests and dead UI. No dual-write, no permanent fallback, no TODO debris.
8. Review (`jularr-review`), then finish (`jularr-finish-task`). Close an issue only when every acceptance criterion is demonstrably met; otherwise comment the exact remaining gap.
