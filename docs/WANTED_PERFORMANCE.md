# Wanted read performance

The Wanted list, its summary and the automatic search scheduler read current database
state. Migration `0002_wanted_present_asset_projection.sql` changes the canonical
`WantedUnitsBelowCutoff` view to reduce assets to existing files before expanding profile
quality orders. Its quality, format-score, language, platform and monitoring rules remain
the same. No tables, indexes or stored data change, and there is no persistent cache.
The cutoff result is materialized once per statement; episode upgrade status is also
calculated once per target instead of once per episode. File existence
checks use parameterized asset probes (`OFFSET 0` keeps PostgreSQL from flattening them
into repeated inventory scans), and unit probes include both columns of the existing
`MediaAssets (WorkId, WorkUnitId)` index.

These four short reads also opt out of JIT through `SqlStatement.DisableJit`.
`SqlContext` applies `SET LOCAL jit = off` once per transaction; the connection's
session setting is restored on commit or rollback. This is the identical shared
infrastructure change used by the library optimization in PR #1527. Database-wide
JIT settings and other statements are unchanged. Materialization alone is insufficient:
after auto-analysis, the cutoff view can have a much higher estimated cost and trigger
expensive JIT compilation again.

## Isolated measurement

`WantedPerformanceTests.Wanted_MixedInventory_ReportsWarmReadLatency` creates a disposable
database on the guarded test server. Its inventory contains 411 works, 11,100 episodes,
146 assigned files and 3,365 unmatched files, paused and high-priority targets, and two
library roots. It measures authenticated API reads and, separately, the scheduler,
100 episode states and one target. No indexer, scan or download is run.

```powershell
$env:JULARR_TEST_DB='Host=127.0.0.1;Port=15433;Username=jularr_test;Password=isolated-test-password;Database=jularr_test_admin'
$env:JULARR_TEST_WANTED_PERFORMANCE='1'
$env:JULARR_TEST_WANTED_SAMPLES='100'
$env:JULARR_TEST_WANTED_MAX_MS='100'
dotnet test tests/Jularr.Backend.Tests/Jularr.Backend.Tests.csproj -c Release --filter FullyQualifiedName~WantedPerformanceTests --logger 'trx;LogFileName=wanted-performance.trx'
```

The test skips without opt-in. By default it leaves initial statistics untouched;
`JULARR_TEST_WANTED_ANALYZE=1` also tests analyzed tables. Each case has two warmups.
The TRX attaches exact `EXPLAIN (ANALYZE, BUFFERS)` plans and JSON responses for comparison.
API timings are the service timings; scheduler/target/unit timings include opening and
committing their read-only transaction. Neither measures browser/network latency.

The initial API baseline (10 measured reads per view, i5-1145G7, local PostgreSQL 18)
had P95 3,090 ms for the summary and 3,073 ms for the cutoff list. Plans showed repeated
cutoff evaluation, inventory scans and hundreds of JIT functions. Checking only one
library root hid another unfavorable planner choice, so the final fixture includes two.

Final analyzed inventory, 100 measured reads per case:

| Read | Median ms | P95 ms |
| --- | ---: | ---: |
| Summary | 41.1 | 63.0 |
| All targets | 25.4 | 35.5 |
| Missing | 34.7 | 53.8 |
| Cutoff unmet | 25.1 | 37.7 |
| Scheduler selection | 41.0 | 70.2 |
| 100 episode states | 17.2 | 23.0 |
| Single target | 7.3 | 10.8 |

All seven response snapshots match the pre-change JSON byte for byte. The release
migration follows the existing backup-first startup transaction. Integration tests cover
version recording, retained data, failed backups, rollback and the frozen baseline hash;
they use a fake dump tool and do not constitute a production upgrade rehearsal.

These measurements do not establish production latency on the i5-6500T, concurrent
load, external-provider response times or bulk-job completion time. Active databases
are never used by this benchmark.
