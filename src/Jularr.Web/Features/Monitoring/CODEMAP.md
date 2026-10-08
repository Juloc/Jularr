# Monitoring code map

One monitoring system for every media type. A decision says what the owner wants; the effective state is derived, never stored twice.

## Model (`MonitoringModels.cs`)
- `WorkMonitoring` rows: `Kind` (Work, Season, Episode, Volume, Chapter, Track), `TargetId` (unique), `Monitored`.
  No row means Inherit. There is no persisted mode ("future", "all"), no timestamp, no tags.
- `WorkMonitoringSources` rows: a relation to follow (Person with role filter, Studio, Collection, Artist), `Roles`, `Label`.
- `WorkMonitoringView`: the read model of one Work. `IsMonitored(node, parent)` is the single answer.

## Resolver (`MonitoringResolver.cs`)
Effective state of a node = its own decision, else its season/volume decision, else the Work decision, else a monitored
relation source that reaches the Work (`RelationCoveredWorksSql`), else unmonitored. Relations only add monitoring;
an explicit Unmonitored decision on a concrete target beats them. `MonitoredWorkIdsAsync` pages the Works the Wanted
pass must look at (any node switched on counts).

## Commands / store (`MonitoringCommands.cs`)
- `SetAsync(kind, id, bool?)`: Monitored / Unmonitored / null = Inherit. A Work or season decision replaces the decisions below it.
- `SetManyAsync`: bulk, one request, one transaction, set-based SQL.
- `FutureAsync(workId)`: Work monitored, aired episodes and all known chapters/tracks explicit Unmonitored, later ones inherit.
- `ApplySelectionAsync`, `SetEpisodesByNumberAsync`, `SetSeasonsByNumberAsync`: request dialog and anime (addressed by number).
- `SetRelationAsync(source, monitored, onlyFuture)`: add or remove a relation source.

## Wanted
Monitoring only states intent. `Features/Acquisition/Wanted` (see its CODEMAP) reconciles it into `WantedItems` and the engines read that queue.
Books, Light Novels and Manga still use `MonitoringWantedSource` until they move onto it.

## API (`MonitoringEndpoints.cs`, `/api/monitoring/v1`)
`GET works/{id}`, `PUT targets/{kind}/{id}`, `POST works/{id}/future`, `PUT relations/{kind}/{key}`.
`MonitoringFollower` runs the follow-up (wake the request or end it when nothing is monitored).

## Callers
- Video: `Features/Acquisition/Monitoring/VideoMonitoringService.cs`, `Access/VideoRequestScopeResolver.cs` (request dialog choice).
- Anime: `Features/Acquisition/Monitoring/AnimeMonitoring.cs` (anime key to Work, season/episode numbers).
- Music: `Features/Music/MusicLibraryService.cs` (artist = relation source, album = Work decision), `MusicQuery.cs`.
- Readers: `ConsumerAcquisition`, `VideoDetail`, `InstantPlay/VideoPlaybackFactsQuery`, `WantedListService`, `VideoUpgradeWantedSource`.

## Not built (needs a product decision)
Author / Book series / Label / Franchise entities, recording-level Music, Audiobook chapters, per-unit Wanted for reading kinds.

## Tests
`CanonicalMonitoringTests` (states, inheritance, Future, relations, bulk, tracks), `CanonicalMonitoringMigrationTests`
(old payload/Music data), `VideoAdminSurfaceTests` (admin switches), `MonitoringTestSupport` (shared builders).
