# Library read performance

`LibraryPerformanceTests.Library_MixedInventory_ReportsWarmReadLatency` measures the real
`ReadLibraryV1` service through the authenticated HTTP adapter, with 48 cards per page.
It records the first request separately, skips two warm-up requests, then reports the
median and p95 of both the service and TestServer HTTP calls. SQL plans and card JSON
are attached to the TRX result. It uses the guarded, disposable `TestPostgres` databases.

## Reproduce

Use the dedicated PostgreSQL 18 test server on `127.0.0.1:15433` with user `jularr_test`
and maintenance database `jularr_test_admin`; set `JULARR_TEST_DB` to its connection string.
Build the test project in Release, then run:

```powershell
$env:JULARR_TEST_LIBRARY_PERFORMANCE = '1'
$env:JULARR_TEST_LIBRARY_SAMPLES = '100'
$env:JULARR_TEST_LIBRARY_ANALYZE = '0'
$env:JULARR_TEST_LIBRARY_MAX_MS = '100'
dotnet test tests/Jularr.Backend.Tests --configuration Release --filter FullyQualifiedName~LibraryPerformanceTests --logger 'trx;LogFileName=library-performance.trx'
```

The default synthetic inventory has 411 works (300 films, 111 series), 11,100 episodes,
146 assigned files, 3,365 unmatched files, audio/subtitle tracks and per-profile progress.
The application connection pool is enabled. `JULARR_TEST_LIBRARY_ANALYZE=0` deliberately
includes the poor-statistics case after a bulk import. Omit it to analyze the seeded tables.
`JULARR_TEST_LIBRARY_STRESS=1` instead seeds 2,120 works and 14,000 assigned files.
The optional maximum is enforced for every service p95; ordinary CI skips this opt-in test.

## Local comparison, 23 October 2026

PostgreSQL 18 in Docker, .NET 10 Release, Intel Core i5-1145G7. Original source:
`6eb427c2996d30b1ebbdbcd1a85717700c689e42`. Both runs used the default inventory above
without explicit `ANALYZE`; before has 20 measured calls per case, after has 100.

| Read | Before service p95 | After service p95 |
| --- | ---: | ---: |
| Recently added | 1,999.0 ms | 74.6 ms |
| Title | 1,929.2 ms | 80.7 ms |
| Year | 2,022.2 ms | 70.5 ms |
| Progress | 2,413.7 ms | 74.9 ms |
| Series filter | 2,278.1 ms | 96.7 ms |
| Second page | 2,014.8 ms | 69.2 ms |
| Title search | 2,393.9 ms | 96.4 ms |

The seven card JSON snapshots are byte-identical before and after. The changes
aggregate dates and languages together, share unit-progress counts between ordering and
cards within one query, and keep profile/file lookups parameterized by their selected ids.
The library disables PostgreSQL JIT only for its own transaction: poor cost estimates
previously triggered hundreds of compilation functions for a short interactive read.
Commit/rollback and pooled-connection tests verify that this setting cannot escape.

These are local service measurements, not live acceptance on the Owner's i5-6500T.
The HTTP p95 was 74.3–104.5 ms; internet latency, full HTML rendering, cover requests,
production concurrency and real scan/download load need their own live verification.

## Library filters and infinite scrolling

The filter package intentionally changes the default result set: only Works with a present
non-subtitle file are listed. Own/other active requests are optional union choices. Genre,
audio/subtitle language, progress and minimum rating filter the candidates before pagination.
Languages use shared sets of Works with present tracks, avoiding a file scan per candidate.
`ReadLibraryFilterOptionsV1` reuses the same visibility/availability boundary. Appended pages
skip that extra options read; pagination still reads at most 48 cards, with no total count.

The expanded benchmark covers nine card reads and the options endpoint. On the same local
PostgreSQL 18/i5-1145G7 setup, .NET 10 Release, 100 measured calls per case in each run,
with and without explicit `ANALYZE`, and no parallel builds/tests:

| Read | Service p95, no ANALYZE | Service p95, ANALYZE |
| --- | ---: | ---: |
| Recently added | 33.0 ms | 85.0 ms |
| Title | 33.7 ms | 84.0 ms |
| Year | 41.2 ms | 61.6 ms |
| Progress order | 40.8 ms | 64.7 ms |
| Series | 39.5 ms | 31.0 ms |
| Second page | 53.0 ms | 32.4 ms |
| Genre + audio + subtitles | 33.8 ms | 71.2 ms |
| Progress filter | 37.3 ms | 35.7 ms |
| Title search | 35.4 ms | 35.5 ms |

HTTP p95 for these reads was 31.2–91.6 ms across both runs; options HTTP p95 was
13.7/11.5 ms without/with ANALYZE. These results
describe the new available-files scope, so they are not snapshot-equivalent to the original
performance-only comparison above. Earlier runs during builds showed outliers above 100 ms;
this remains a local read benchmark, not a production concurrency or full-page SLA.

Focused verification: `MediaTests`, `PagingTests`, the Discover request flow and Plex sync
integration (27 tests); `LibraryFiltersTests` exercises SSR filters, automatic append,
failure/retry, Back restoration, duplicate-free cards, mobile drawer and native paging without
JavaScript. No production database or schema change is part of the filter package.

## Current availability and content languages

`WorkCardProjection` evaluates present files before acquisition labels, using every version
and the canonical additional-unit coverage. A paused missing target is distinct from one
awaiting another search; an existing file stays available during an upgrade. Library,
Favorites, details and Discover's bounded `/api/v1/library/availability` overlay share
that projection. Provider metadata stays cached; local status is read at display time.

Profiles may choose a first and a second content language in Settings. Either is accepted;
menu locale and acquisition quality settings stay independent. Video/audio uses present
tracks, books use current parsed document language or their file's edition language.
Unknown language stays neutral. Matching subtitles have their own complete/partial states.
Migration `0011_profile_content_languages` adds nullable preferences without inferring a
choice for existing profiles. Regression coverage: `ProfileReferenceVerticalTests`,
`MediaTests.WorkCard_*`, `DatabaseMigrationTests.ContentLanguages_*` and
`ContentAvailabilityTests`. The benchmark now enables both content languages and measures
the bounded Discover availability overlay as well as the existing Library reads.

Verification on 26 October 2026: isolated local PostgreSQL, .NET 10 Release, 411 Works
with 11,100 episodes, both content languages set, 100 measured calls after two warmups
per case, no explicit ANALYZE and no concurrent builds/tests. All nine Library reads
passed the 100 ms p95 budget: service p95 31.7–46.2 ms, HTTP p95 37.1–51.2 ms.
The mixed 20-Work availability overlay was 23.1 ms HTTP p95; filter options 15.3 ms.
These are local read measurements, not a production concurrency or full-page guarantee.
