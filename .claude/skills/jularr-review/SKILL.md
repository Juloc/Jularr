---
name: jularr-review
description: Diff-oriented self-review of a Jularr change against ownership, security, concurrency and maintainability rules before commit. The lead agent reviews its own diff; no subagent by default.
---

# Jularr review

Run before every substantial commit. Review `git diff` (working tree) or `git diff origin/dev...HEAD` yourself, only the changed and directly affected ownership. This is not a repo-wide audit.

Do not spawn a review subagent by default. A separate reviewer is justified only when the user asks for it, the change is very high-risk and cross-domain, or a hard concurrency/schema-ownership question deserves independent reasoning worth the cost.

Check:

- **Ownership**: one canonical owner per responsibility; no duplicate state, schedulers, progress models, config paths, request/acquisition pipelines or player architectures; Movie/TV not copied from Anime.
- **Identity**: media identity is Work/WorkEpisode/Asset/File ids, never a raw filesystem path; no new use of legacy Anime-only bridges (`LegacyWorkBridge`, `WorkSourceLink`, `EpisodeProgress`) beyond migration.
- **Auth**: every page handler/endpoint checks capability/permission server-side; progress is profile-scoped; no secrets, tokens or host paths in DTOs or logs.
- **Concurrency/idempotency**: retries, double submits, restart recovery, unique constraints, transaction scope, no transaction across external I/O.
- **Errors/cancellation**: no swallowed exceptions or catch-all-return-false; `CancellationToken` and timeouts on I/O; expected states are results, not exceptions.
- **DB efficiency**: N+1, application-side filtering of whole tables, redundant indexes, cascades used as behavior.
- **Hidden effects**: reads or validators that mutate; names that do not match behavior.
- **Abstraction**: interface/factory/manager/forwarding wrapper without a current concrete need; artificial mini-methods; lines over 280 characters; ordinary calls vertically fragmented under 230.
- **Legacy**: replaced paths removed, no commented-out code or vague TODOs, no fallback masking a broken canonical path.
- **Tests**: focused behavior/regression coverage exists, deterministic, PostgreSQL behavior tested on PostgreSQL.
- **UI** (if applicable): matches SPEC and approved images, all states, accessibility, Light/Dark, mobile; no duplicated or filler text.

Review does not mean running more tests. If adequate focused green tests exist, inspect them and stop; run an extra test only for a specific uncovered failure mode the review found, never the full suite.

Report each finding with file:line where practical, a concrete failure scenario and a severity. Separate confirmed defects from suggestions and report only what the code justifies. Fix confirmed defects in your own diff before committing.
