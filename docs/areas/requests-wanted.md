# Requests and Wanted

User requests, moderation, monitoring targets and the Wanted list.
Index: [CODEMAP](../CODEMAP.md).
Specs: [WANTED_PERFORMANCE](../WANTED_PERFORMANCE.md).

## Decisions

Owner decisions; change them only with the owner.

- A Wanted row's state pill is a short word (*Requested* while nothing came); why the last search found nothing (the download storage is unavailable,
  nothing fits the profile) stands whole in a small muted line under the pill, never cut off inside it.
  A target's own page keeps the whole sentence as its state.
- **Wanted** has the views *Missing* (the default), *Cutoff unmet* and *All* (last, the only one that also lists available targets).

## Backend

- [User request services](../../src/Jularr.Backend/Service/User/Request/V1/RequestServices.cs), [Admin request
  services](../../src/Jularr.Backend/Service/Admin/Request/V1/RequestServices.cs), [Wanted
  services](../../src/Jularr.Backend/Service/Admin/Wanted/V1/WantedServices.cs): profile-scoped language intent, bounded status queues, moderation and
  manual pause/resume; episodes and volumes carry `IsMonitored` (specials start off, a season switches its episodes, new units follow
  `WantedItems.MonitorsNewUnits`) and only monitored units are searched; a request completes once nothing monitored is missing, also after
  unmonitoring.
  The list filters Missing and Cutoff unmet (`WantedViewType`), the cutoff decided by the view `WantedBelowCutoff`
  ([Library.sql](../../database/Library.sql)) as `UpgradePolicy.Assess` does; priority High/Normal per target (`WantedItems.WantedPriorityTypeId`;
  `UpdateWantedPriorityV1` sets it for many, `StartWantedSearchV1` brings the chosen targets forward and wakes the search; `ReadDueWantedItemsAsync`
  takes High first and never waits: tests `AcquisitionTests.WantedPriority_*`); whole Works below it are due again every
  `UpgradePolicy.SearchInterval`, and a release that is no upgrade of the best present quality is only taken by hand (`NotAnUpgrade`).
  Request rules ([RequestRuleServices.cs](../../src/Jularr.Backend/Service/Admin/Request/V1/RequestRuleServices.cs),
  [RequestRuleLogic.cs](../../src/Jularr.Backend/Logic/Acquisition/RequestRuleLogic.cs)): rule profiles with limit per rolling period, approval,
  allowed media and the qualities requesters may choose, one default; accounts inherit the default or an assigned rule and override single values; the
  view `EffectiveRequestRules` resolves them.
  A new request locks its account, checks media, offered quality and limit (repeated intent does not count) and is approved at once for the Owner (not
  for a device paired to the Owner) or an automatic rule; a new Wanted target takes the requested quality, an existing one keeps its own (one version
  and profile per Work).
- [AcquisitionRequestLogic.cs](../../src/Jularr.Backend/Logic/Acquisition/AcquisitionRequestLogic.cs),
  [WantedLogic.cs](../../src/Jularr.Backend/Logic/Acquisition/WantedLogic.cs): idempotent creation, Open decisions and atomic shared whole-Work
  targets.
  A profile's `ReadRequestsV1` narrows its requests by status view (`RequestListFilterType`), media type and order (`RequestSortType`) and says which
  are withdrawable; `WithdrawRequestV1` lets the requester take back their own request until it is imported (waiting, queued, searching, downloading,
  importing; afterwards only an administrator changes it): the request becomes Cancelled and a target that no other request waits for and that never
  imported anything is paused; running downloads stay with the administrator's queue (`RequestTests`).
  [Acquisition.sql](../../database/Acquisition.sql): explicit fresh baseline after Account/Media.
- Monitoring a title without target: `StartWorkMonitoringV1` (`Service/Admin/Wanted/V1/WorkMonitoringServices.cs`) creates the target in the library
  of its files and wakes the search; the Work hero offers it through `StartMonitor` (`Pages/Admin/Work/Work.Management.cs`); tests in `WorkListTests`.
  The `wantedTargets` job (`EnsureWantedTargetsV1` in `Service/System/Media/V1/WantedTargetServices.cs`, run by `MediaWorker`) gives every title with
  files a paused target in the library of its files, so every title page has the same buttons; test `WorkListTests.Targets_*`.

## Frontend

- [Requests](../../src/Jularr.Frontend/Pages/User/Requests/Requests.cshtml.cs),
  [styles](../../src/Jularr.Frontend/Pages/User/Requests/Requests.entry.scss), [client](../../src/Jularr.Frontend/Pages/User/Requests/Requests.ts):
  the profile's requests, one compact row each (cover, name, type and day, language and quality, live steps, Open/Details) or two-column cards; one
  row of status tabs with the order menu and the layout switch, then the shared media-type bar (all addresses: `state`, `type` as `a,b` or repeated,
  `sort`, `view`, `p`); the side panel of one request (`?request=`) with its steps, facts, activity and withdrawing it after a confirmation;
  authorized source confirmation.
  `RequestTests`, `requests.test.mjs`.
- [Requests](../../src/Jularr.Frontend/Pages/Admin/Requests/Requests.cshtml.cs),
  [Wanted](../../src/Jularr.Frontend/Pages/Admin/Wanted/Wanted.cshtml.cs) (list with the priority tag; the chosen rows join the form `wanted-bulk`,
  whose priority and search buttons live in the [ActionDock](../../src/Jularr.Frontend/Framework/Components/ActionDock.cs)'s bulk zone;
  `AutomaticPathTests.WantedPriority_*`), one [target](../../src/Jularr.Frontend/Pages/Admin/WantedItem/WantedItem.cshtml.cs) per page (status with
  searches, profile, library and shared Work tags, seasons and episodes each searched automatically or by hand, releases) and its [manual
  search](../../src/Jularr.Frontend/Pages/Admin/WantedSearch/WantedSearch.cshtml.cs) (a dialog over the page that links to it,
  `a[data-search-dialog]`, and the same panel as the page: [handlers](../../src/Jularr.Frontend/Pages/Admin/WantedSearch/WantedSearch.cshtml.cs)
  `Dialog`, `Source` (one source's `tbody`), `Grab`; [panel and rows](../../src/Jularr.Frontend/Pages/Admin/WantedSearch/WantedSearch.Results.cs),
  [script](../../src/Jularr.Frontend/Pages/Admin/WantedSearch/WantedSearch.entry.ts) and
  [styles](../../src/Jularr.Frontend/Pages/Admin/WantedSearch/WantedSearch.scss), loaded by the shell; score with its points on hover, the release
  group; books add kind, volumes and a size mark; the filter bar (title words, language, protocol, age, size, quality as a checkbox popup, decision,
  sort) works on the loaded rows in the script, the candidate carries `ReleaseProtocolId` and `Languages` for it, the rows `data-title`,
  `data-quality` and `data-decision`; `WantedSearchDialogTests`, `ManualBookSearchTests`), Wanted is an entry of its own in the sidebar's ARR group
  after the Queue (the next bullet), its views Missing (default), Cutoff unmet and All: moderation, acquisition and recovery; the admin Work links to
  its target.
- [Request users](../../src/Jularr.Frontend/Pages/Admin/Users/Users.cshtml.cs), [Rules](../../src/Jularr.Frontend/Pages/Admin/Rules/Rules.cshtml.cs):
  rule list and editor; accounts with their resolved rule, assignment and overrides.
  Details offers the rule's qualities when requesting.

## Tests

- Wanted SQL latency: [measurement and reproduction](../WANTED_PERFORMANCE.md), opt-in `WantedPerformanceTests`; behavior in
  `AcquisitionTests.WantedViews_*` and `AcquisitionTests.WantedPriority_*`.
- Request isolation/idempotence, bounded reads, moderation rollback/shared targets and real Razor forms/module gating: `RequestTests.cs`.
