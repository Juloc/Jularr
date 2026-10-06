# Acquisition Search Planner — V1

Status: approved planning direction.

Owner issue: #864  
Parent: #396  
Related UI specs:
- `docs/mockups/admin-manual-search/SPEC.md`
- `docs/mockups/admin-wanted/SPEC.md`
- `docs/mockups/admin-acquisition-settings/SPEC.md`

This specification defines **how Jularr searches for release candidates**. It does not define whether a returned release is acceptable; that is owned by `docs/AUTOMATIC_RELEASE_SELECTION.md` and the Acquisition Profile.

If an older media-specific acquisition document conflicts with this file, this file wins for generic search-planning semantics.

## Core rule

Automatic Search and Manual Search use one canonical planner:

```text
Canonical acquisition target
-> SearchIntent
-> canonical IDs/titles/aliases/numbering
-> indexer capability + participation policy
-> staged QueryPlan
-> bounded execution
-> normalize results
-> conservative cross-indexer dedupe
-> preserve query/source provenance
-> identity/coverage evaluation
-> candidate set
```

Search planning never makes a candidate acceptable by itself.

## Shared core, media-specific strategies

Do not create separate search engines for Anime, Series, Movie, Book, Audiobook, Manga or Light Novel.

Use:

- one shared planner/executor;
- one shared provider/indexer capability model;
- one shared cache/budget/dedupe/provenance model;
- small media-specific query strategies that translate canonical target identity into query variants.

Current owner decision remains **Usenet-only**. Direct Newznab is first-class; Prowlarr may remain as a compatibility/aggregation adapter.

## Capability-aware planning

On indexer add/test/refresh, retain the useful capabilities required to build safe queries.

Where reported/configured, include:

- generic search support;
- movie search support;
- TV/episode search support;
- book/other advertised search modes;
- supported external-ID parameters;
- supported season/episode parameters;
- categories/custom categories;
- media-scope compatibility;
- paging/result-limit behavior;
- authentication requirements;
- configured/provider query/grab limits.

Never send a parameter merely because another provider supports it.

A successful capability request does not by itself mean an indexer is useful. Track distinct states:

- reachable;
- authentication valid;
- capabilities parsable;
- representative search parsable;
- recent searches successful;
- current/fresh enough to be useful.

Capability/category drift must preserve explicit owner overrides and warn rather than silently remap media kinds.

## Indexer participation modes

Each indexer can independently participate in:

- **Automatic / Wanted search**
- **Interactive / Manual Search**
- future recent-feed/background discovery only if such a mode is explicitly introduced

Manual-only indexers are valid.

Media-scope restrictions must prevent unrelated searches. Unsupported media kinds are skipped with a visible reason, not searched through a random fallback category.

## Query ladder

Use strongest structured evidence first, then expand carefully.

Only generate fields/IDs the target indexer supports.

### Movie

1. trustworthy supported external ID such as TMDB/IMDb;
2. canonical title + year;
3. original/localized/alternate title + year;
4. normalized title fallback.

### TV / Series

1. trustworthy supported TV provider ID + season/episode;
2. canonical title + `SxxExx`;
3. alternate title + `SxxExx`;
4. supported date/daily or other canonical structural numbering where applicable.

### Anime

1. supported canonical anime/TV ID where available;
2. canonical mapped season/episode;
3. canonical mapped absolute episode;
4. approved Romaji/English/native aliases with canonical numbering variants;
5. conservative title fallback.

Anime query planning uses canonical mapping evidence for cour/season/absolute/specials. It must not infer canonical numbering merely from one external release title.

### Book / Audiobook

1. trustworthy supported identifier where available;
2. title + author;
3. edition/narrator/language-aware variant where meaningful;
4. conservative title fallback.

### Manga / Light Novel

1. trustworthy supported provider/series ID where available;
2. canonical series title + volume/chapter target;
3. approved alias + target;
4. conservative title fallback.

Do not issue every alias/variant at once.

## Adaptive expansion

Query execution is staged.

User-facing Search Depth:

- **Fast** — strongest structured query set only;
- **Normal** — default automatic behavior; expand when insufficient usable diversity remains;
- **Deep** — explicit Admin/Manual troubleshooting mode with additional aliases, fallbacks and pagination.

Automatic search defaults to Normal.

Deep must not silently become a background default.

Stop expanding when enough **distinct usable candidate diversity** exists. Raw duplicate count does not satisfy the stop condition.

## Search budgets

Every pass has explicit bounds:

- maximum concurrent indexers;
- per-indexer timeout;
- maximum query variants per target;
- maximum pages/results per query/pass;
- configured query/grab budgets;
- Retry-After/rate-limit handling;
- provider backoff/circuit breaker.

One failed/slow/rate-limited indexer cannot invalidate successful results from others.

Distinguish:

- provider/indexer failure;
- no results;
- candidates found but identity-invalid;
- candidates found but profile-rejected.

These states drive different retry/backoff behavior.

## Pagination

Do not assume the first page is enough.

Normal/Deep may continue while:

- provider supports it;
- budget remains;
- new distinct candidates are appearing;
- usable candidate diversity remains insufficient.

Stop when results become duplicate-only/stale or the search budget is exhausted.

## Search-result cache

Use short-lived caching to prevent repeated identical provider calls.

Rules:

- cache raw/normalized candidate evidence, not intrinsic profile scores;
- rescoring after profile/language changes should reuse still-valid candidate evidence;
- cache key includes target/query/indexer-relevant context;
- bounded negative-query cache/backoff prevents repeatedly executing an identical known-empty Deep query;
- explicit authorized refresh can bypass appropriate cache layers;
- TTL/bounds must not hide newly available content indefinitely.

## Cross-indexer deduplication

Equivalent releases returned by multiple indexers should become one logical candidate with multiple source options when confidence is sufficient.

Prefer evidence such as:

- stable release GUID/provider ID;
- normalized release title;
- size;
- release-group facts;
- timestamps/other normalized facts.

Do not merge aggressively by title alone.

Preserve:

- every source/indexer;
- grab URI/reference;
- source health;
- source priority;
- query provenance.

Selection/download fallback may choose another source without duplicating the logical candidate.

## Query provenance

Every candidate retains enough evidence to explain:

- indexer/provider;
- QueryPlan stage;
- structured ID or title/alias used;
- numbering form used;
- category/search mode;
- structured-ID vs text fallback;
- raw candidate source options merged by dedupe.

Manual Search should be able to show concise provenance such as:

`Found via TVDB + S01E03 · Indexer A`

and a deeper Search Trace.

Never expose API keys, secret-bearing URLs or unsafe raw request data.

## Manual Search contract

Manual Search may expose:

- Fast / Normal / Deep selector;
- search trace;
- partial indexer errors;
- rate-limit/backoff state;
- raw result count vs deduplicated candidate count;
- source list for deduplicated releases;
- explicit Refresh;
- explicit Deep Search.

Changing search depth or refreshing does not mutate the Work's persistent Acquisition Profile.

## Automatic search contract

Automatic search:

- uses the same planner as Manual Search;
- uses Normal depth unless explicitly configured otherwise;
- obeys per-indexer Auto participation;
- obeys scheduler/backoff/search budgets;
- does not run Deep alias fan-out by default;
- never uses a profile preference rule to rewrite canonical target identity;
- hands normalized candidates to the canonical automatic-selection engine.

## Provider/indexer diagnostics

Admin should see capability facts in human terms where known, for example:

`Movie ID ✓ · TV ID ✓ · Season/Episode ✓ · Anime category ✓ · Manual ✓ · Auto ✓`

Also show:

- last capability refresh;
- last successful representative search;
- current backoff/rate-limit state;
- stale-result/usefulness warning;
- media kinds/categories mapped.

## Testability

Deterministic coverage must include:

- structured-ID query;
- unsupported-ID fallback;
- title aliases;
- season/episode numbering;
- anime absolute numbering;
- keyless/authenticated Newznab;
- partial indexer failure;
- rate limiting;
- pagination;
- result dedupe with preserved provenance;
- false-positive dedupe avoidance;
- positive and negative cache behavior;
- capability/category drift;
- stale-but-reachable provider;
- deterministic query-plan order.

Live provider checks are supplementary smoke tests only.

## Must not implement

- no separate Auto and Manual query engines;
- no separate per-media acquisition/search cores;
- no blind `title S01E01` as the only TV/anime strategy;
- no assumption that all Newznab-compatible indexers accept identical parameters;
- no Deep fan-out for every automatic pass;
- no provider-specific candidate object becoming canonical media identity;
- no profile score stored as a permanent property of a cached release;
- no title-only aggressive dedupe;
- no hidden query URLs containing credentials in UI/history/logs.

## Acceptance

- one canonical Search Planner serves Auto + Manual;
- capabilities drive query construction;
- structured IDs are preferred when supported/trustworthy;
- query ladders exist for all supported media kinds;
- expansion is adaptive and bounded;
- Auto/Manual indexer participation is independent;
- rate limits/timeouts/backoff/pagination are explicit;
- result caching is bounded and score-independent;
- cross-indexer dedupe preserves provenance/fallback;
- Manual Search can explain how every candidate was found.
