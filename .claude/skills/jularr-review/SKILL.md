---
name: jularr-review
description: Independent review of a Jularr change against architecture, ownership, security and maintainability rules before it is committed. Use on the diff of every substantial slice, ideally from a fresh subagent.
---

# Jularr review

Review `git diff` (working tree) or `git diff origin/dev...HEAD` and report findings with file:line, a concrete failure scenario and severity. Check:

- **Ownership**: one canonical owner per responsibility; no duplicate state, schedulers, progress models, config paths, request/acquisition pipelines or player architectures; Movie/TV not copied from Anime.
- **Identity**: media identity is Work/WorkEpisode/Asset/File ids, never a raw filesystem path; no new use of the legacy Anime-only bridges (`LegacyWorkBridge`, `WorkSourceLink`, `EpisodeProgress`) beyond migration.
- **Auth**: every page handler/endpoint checks capability/permission server-side (direct URL/API access by non-admins to Admin is denied); progress is profile-scoped; no secrets, tokens or host paths in DTOs or logs.
- **Concurrency/idempotency**: retries, double submits, restart recovery, unique constraints, transaction scope, no transaction across external I/O.
- **Errors/cancellation**: no swallowed exceptions or catch-all-return-false; `CancellationToken` and timeouts on I/O; expected states are results, not exceptions.
- **DB efficiency**: N+1, application-side filtering of whole tables, redundant indexes, cascades used as behavior.
- **Hidden effects**: reads or validators that mutate; names that do not match behavior.
- **Abstraction**: new interface/factory/manager/forwarding wrapper without a current concrete need; artificial mini-methods; lines over 280 characters; ordinary calls vertically fragmented under 230.
- **Legacy**: replaced paths removed, no commented-out code or vague TODOs, no fallback masking a broken canonical path.
- **Tests**: behavior/regression coverage, deterministic, PostgreSQL behavior tested on PostgreSQL.
- **UI** (if applicable): matches SPEC and images, all states, accessibility, Light/Dark, mobile; no duplicated or filler text.

Report only what can be justified from the code and separate confirmed defects from suggestions.
