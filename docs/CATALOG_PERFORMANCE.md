# Local Discover read performance

The approved scope is to reduce the remaining slow local Discover reads without changing ranking, paging, profile visibility, state or image ownership. Provider network calls and production deployment are excluded. This package builds on the unmerged library performance branch (#1527); its shared card projection already improves the Work detail controls below.

`ExternalWorkLogic.ReadExternalWorkShelvesAsync` serves both feed endpoints and the batched shelves endpoint. It now resolves the indexed Work/season identities before consulting the canonical `ProfileWorks` and `ProfileWorkStates` views. Lateral `OFFSET 0` barriers keep those lookups parameterized. A union preserves the previous membership semantics when both identity routes resolve the same Work. The statement and `ReadWorkV1` disable JIT only within their transactions using the existing SQL-context option. `ReadDiscoverShelvesV1` reads module state, scopes and shelves in one centrally authorized local batch, reducing commands from 15 to 11. There are no schema, index, contract or visibility-rule changes.

The first successful `CompleteExternalWorkSyncAsync` now prepares `ExternalWorks` planner statistics with `ANALYZE` before its transaction exposes the completed catalog. The atomic update's affected-row count ensures this happens only on initial completion, not each worker pass. This work belongs to the background import; user reads do not run maintenance. The benchmark publishes its seeded catalogs through that same completion owner.

## Evidence, 23–24 October 2026

Release build, isolated PostgreSQL 18 on loopback, local i5-1145G7. The fixture has 411 Works, 11,100 episodes, 3,511 files including unmatched files, and 110,000 catalog entries: 50,000 films, 50,000 anime and 10,000 books. Pooling is enabled. Each case excludes two initial calls. Baseline: 30 warm calls with analyzed statistics. Both final variants use 100 warm calls: one explicitly analyzes all fixture tables; the other relies only on the production catalog-completion statistics preparation, with no additional global `ANALYZE`. No other local builds/tests are started concurrently.

| Authenticated read | Before P95, ms | After, all stats | After, catalog completion only |
| --- | ---: | ---: | ---: |
| Film details | 15.4 | 24.8 | 14.7 |
| Series with files | 15.8 | 16.1 | 14.1 |
| Series without files | 15.5 | 15.4 | 14.6 |
| Film trending | 149.0 | 83.6 | 77.8 |
| Anime trending | 147.9 | 47.4 | 46.1 |
| Film top | 128.1 | 38.6 | 39.6 |
| Anime top | 130.2 | 84.2 | 95.6 |
| Film new | 116.6 | 38.2 | 36.9 |
| Anime new | 127.2 | 46.0 | 46.0 |
| Film upcoming | 132.3 | 34.9 | 38.2 |
| Books trending | 113.4 | 38.4 | 39.3 |
| Film trending page 2 | 126.0 | 42.4 | 38.9 |
| Batched shelves, 160 entries | 358.2 | 36.9 | 38.7 |

All thirteen JSON responses were byte-identical to the reference in both final runs. Final HTTP P95 ranges were 21.2–92.5 ms with all statistics and 18.3–103.4 ms with catalog-completion statistics only. The configured 100-ms assertion applies to service P95, not total HTTP/page latency. Before combining the three local batches, repeated 100-call batch results ranged from 58.8 to 106.9 ms P95; a diagnostic 30-call run reached 111.6 ms. Those intermediate failures motivated the service-level batching fix and are not presented as passing final results.

`EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` confirms the causes. The reference film query compiled 186 JIT functions, spending 57.9 ms compiling and 76.1 ms executing overall, versus 4.1 ms execution with JIT off. The reference anime top query still spent 22.0 ms executing without JIT: each of 20 unknown catalog entries scanned 411 local Works. The optimized anime top query executed in 1.4 ms without those scans. Its lower estimated cost also avoided JIT when explicitly enabled in the diagnostic transaction. JIT-off alone was insufficient: an intermediate 100-call anime-top P95 was 101.6 ms.

Artificially marking catalogs complete without preparing statistics produced trending P95 of 114–120 ms: the planner read 50,000 rows through the Released index and sorted them. After canonical completion prepares statistics, the existing Trending index reads 21 rows and the diagnostic SQL executes in 3.4 ms. No ranking rewrite or new index is needed. Normal PostgreSQL statistics maintenance remains necessary for later data changes.

Focused integration tests cover whole-catalog shelves/ranking/paging (including tied ranks across page boundaries), provider fallback when local discovery/cache is disabled, canonical anime matching, Work details, direct and season identities, duplicate identity routes, owned covers, season-specific provider covers, blocked-subtype isolation, and real worker completion with available ranking statistics. No active database was changed. This is a warm local-read budget, not a production, concurrent-load or complete-page latency guarantee; the live i5-6500T still requires measurement after an explicitly approved merge/deployment.

## Reproduce

Set `JULARR_TEST_DB` to the isolated test PostgreSQL server, then:

```powershell
$env:JULARR_TEST_CATALOG_PERFORMANCE = '1'
$env:JULARR_TEST_LIBRARY_SAMPLES = '100'
$env:JULARR_TEST_LIBRARY_MAX_MS = '100'
# Optional SQL-plan artifacts for representative known/unknown title cases:
$env:JULARR_TEST_CATALOG_PLANS = '1'
dotnet test tests/Jularr.Backend.Tests --configuration Release --filter 'FullyQualifiedName~DiscoverAndWork_MixedInventory' --logger 'trx;LogFileName=catalog-performance.trx'
```

`JULARR_TEST_CATALOG_CASE=shelves` selects the batch alone; unset it for all cases. `JULARR_TEST_LIBRARY_ANALYZE=0` skips additional global statistics preparation, retaining the production completion owner's catalog statistics. The test attaches JSON responses and optional SQL plans to its TRX result directory, reports service/HTTP latency, and fails the configured service-P95 budget. The ordinary Library benchmark reuses the same inventory seed.

## Cached-provider fallback before persistent-cache integration, 24 October 2026

The follow-up also measures Discover without a completed local catalog. The same inventory contains only twenty ranked trending entries per source; the remaining catalog rows represent previously discovered titles. Local discovery is disabled and sync completion is not published. In the original measurement, Trending used the fresh database cache and Anime Top/New used the six-hour public metadata cache. The current fixture seeds Top/New through SaveExternalFeedAsync into the persistent feed tables from #1587. Every outbound provider request throws in this mode, so a passing benchmark cannot silently measure a provider response.

The original batching change made `ReadDiscoverCatalogFeedV1` check the canonical profile categories and local-catalog readiness in one centrally authorized batch. Whole-catalog rows, when available, are read in that batch too. `MetadataCatalog` resolves known Works inside the fresh-trending batch and goes directly to the identity lookup for a warm Top/New cache entry. Provider calls remain outside database transactions. These historical measurements retained the then-existing six-hour expiry and stale-read fallback. #1587 subsequently moved renewal to the System worker; reads no longer decide freshness. Cached metadata never stores a profile's access decision or the derived Work/owned-cover mapping.

Release build and analyzed fixture statistics, 100 warm calls per case before and after, excluding two initial calls:

| Authenticated read | Before service P95, ms | After service P95, ms | SQL commands before → after |
| --- | ---: | ---: | ---: |
| Film details | 16.4 | 21.4 | 6 → 6 |
| Series with files | 12.7 | 16.3 | 6 → 6 |
| Series without files | 13.7 | 17.4 | 6 → 6 |
| Film cached trending | 66.6 | 91.0 | 19 → 19 |
| Anime cached trending | 124.9 | 36.3 | 22 → 14 |
| Anime cached top | 93.4 | 25.0 | 19 → 12 |
| Anime cached new | 81.1 | 66.1 | 19 → 12 |
| Books cached trending | 47.9 | 61.0 | 19 → 13 |

All eight JSON responses were byte-identical to the reference. Final HTTP P95 ranged from 21.2 to 94.4 ms. Ten focused Discover integration tests passed, including new coverage for a Work imported after warming Top/New, its current owned cover, blocked Anime access despite a warm cache, and stale Anime trending during provider failure/cooldown. A separate thirteen-case whole-catalog smoke run with SQL-plan artifacts also passed; it is a regression check, not another 100-sample performance result.

The local timing results vary: the first batching stage still failed Top at 114.2 ms P95, and the first run with the final batching failed New at 166.4 ms P95. The final diagnostic run above passed all eight budgets without further runtime changes. It still recorded two individual Film trending calls at 127.7/104.4 ms, with logged SQL totals of 23/33 ms respectively. Consequently this evidence supports lower query/transaction overhead and one passing warm P95 run; it does not establish a stable production or concurrent-load latency guarantee. Provider misses, stale refresh network time and cold statistics are outside this cached-mode result.

To reproduce, add `$env:JULARR_TEST_CATALOG_CACHE_ONLY = '1'` to the command above and leave `JULARR_TEST_LIBRARY_ANALYZE` unset. Unset the cache-only variable to restore the whole-catalog benchmark. Slow measured samples log both service/HTTP time and total SQL time to distinguish query work from other elapsed time.

### Integration with persisted feeds

The batching implementation is already included in #1587. #1561 retains its additional access/owned-cover regression tests and the provider-blocked benchmark without restoring the retired memory-cache or read-time expiry path. Its fixture writes Top/New through the canonical feed writer. Three integration cases passed without skips, including the eight-case benchmark with 100 measured requests per case on the same 411-Work/110,000-catalog-entry isolated inventory.

| Read | Service P95, ms | HTTP P95, ms |
| --- | ---: | ---: |
| Film details | 18.8 | 23.4 |
| Series with files | 22.2 | 27.3 |
| Series without files | 18.3 | 22.0 |
| Film cached trending | 22.4 | 26.5 |
| Anime cached trending | 16.8 | 19.8 |
| Anime cached top | 16.8 | 20.1 |
| Anime cached new | 18.1 | 22.2 |
| Books cached trending | 17.4 | 21.8 |

These are local sequential integration measurements, not production or concurrent-load acceptance. TRX: `cached-discover-persistent-integration.trx`.
