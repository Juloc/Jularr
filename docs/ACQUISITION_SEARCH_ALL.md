# Library Search All

Owner-approved package: [#1645](https://github.com/Juloc/Jularr/issues/1645). This extends the existing acquisition engine; the interactive results dialog remains #1642.

Admin Works offers **Search all**, **Search filtered** and the selection dock's **Search**. All and filtered selections span every page. Enqueue snapshots matching, currently monitored Wanted targets in one transaction; it never enables monitoring. The returned count is the number of search targets, including separate game platforms. An identical active selection returns its existing operation; a different selection waits until that operation finishes or is cancelled.

Works' row and selected-search forms now use the queue. The existing bounded synchronous selected-search API remains available to its existing callers and continues to use the same acquisition engine.

`QueueWorksSearchV1` uses the existing Operations queue with `OperationType.AcquisitionSearch`. One search lane runs alongside scans/backups. Sources retain their existing bounded parallelism, budgets, rate limits and backoff. No database connection stays open while sources answer. The page receives a job immediately, shows persisted progress, and can navigate away or reload. Only its small authorized status fragment refreshes, every two seconds while active and visible. Results use bounded pages of 25.

Every target uses its current effective profile and installed release evidence. Current quality, custom formats, upgrade cutoff and size rules decide whether to search. Unit reassessment includes every episode covered by a file, parses an immutable target snapshot once, and never caches policy decisions. Known total durations allow the same size bounds to apply to stored packs and scoped season-pack candidates. Unknown evidence is reported for review rather than blindly replacing a file. Existing upgrades-disabled and valid-version decisions are retained.

The original requester must still have Wanted-management permission when a target begins. Status, results and cancellation are restricted to that account; module/monitoring checks remain authoritative during execution. Download reservation prevents duplicate or overlapping grabs. Existing downloads are left running, and file replacement remains owned by the existing import path.

Cancellation is persisted before interrupting the current source request. Completed target outcomes survive restart; interrupted operations resume only pending targets. Cancelled runs stay final. Source failures are recorded per target. A failed operation preserves the progress already acknowledged. Finished operations and their results follow the existing history retention policy.

Schema version 12 adds the search run/target tables and the shared `WorkListEntries` projection, keeping list/filter status and queued selection consistent. The version-1 baseline is unchanged. No live database update is part of this package. It follows the profile content-language migration (version 11) merged in PR #1644.

## Source time budgets

The shared acquisition search also serves the synchronous manual-search page.
Each direct book source uses the existing `SearchBudget.IndexerTimeout` for its
whole pass, including a queued Chrome read and all title/ISBN variants: 12 seconds
for Fast, 25 for Normal, and 45 for Deep. Expiry records `source_timeout` and
retains results from sources that answered. If every source failed, the existing
unavailable response is used rather than reporting an empty successful search.
Caller cancellation still cancels the search. All concurrent source tasks finish
before their shared Anna search context is disposed. The browser's global request
timeout is unchanged. See [#1640](https://github.com/Juloc/Jularr/issues/1640).

## Focused verification

- `WorkSearchRunTests`: all-page/filtered/selected snapshot, duplicate click, source cancellation, cross-account denial, durable cancellation/restart, running-download preservation, Razor fragment and warm enqueue latency.
- `SearchReassessmentTests`: forbidden/oversized installed files, allowed replacement, retained valid versions/upgrades-disabled, covered units and known pack duration.
- Existing `WorkListTests` and `ReleaseSelectionTests` protect filter and selection behavior.
- E2E `WorkSearchRunTests`: real background worker, all/filtered/selected forms, navigation/reload, cancellation, results, desktop and 390-pixel mobile layout.
- `BookTests.Book_SlowDirectSource_RespectsSearchBudgetAndRetainsOtherResults` and E2E `ManualBookSearchTests`: a stalled direct source, retained successful results, all-sources timeout and cancellation by navigation.

Three isolated loopback measurements with 500 targets and 20 warm HTTP samples gave p50 **43.4 / 42.8 / 54.2 ms**, p95 **56.1 / 93.4 / 116.2 ms**, maximum **76.8 / 121.5 / 340.5 ms** for enqueue. The third run includes the integrated main/schema-12 changes. These measurements do not establish a consistent sub-100-ms guarantee or a throughput comparison with Sonarr/Radarr. Source response time is excluded. The reproducible latency check is `Queue_500Targets_RecordsWarmHttpLatencyWithoutCallingSources`; hardware, cold start and source response times affect production results.
