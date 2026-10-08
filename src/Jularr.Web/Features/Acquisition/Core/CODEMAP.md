# Acquisition core (search, selection, grab)

Purpose: one search -> rank -> grab path for every media type; adapters only supply facts.

Canonical owners
- `AcquisitionCore`: `SearchAsync` (indexer search, identity judgement, `ReleaseSelectionEngine` ranking) and `GrabAsync` (`ReleaseRequestTracker` lifecycle, `DownloadClientSubmissionService`).
- `ReleaseRanker`: judges and orders releases with `ReleaseSelectionEngine` (also usable without an indexer search).
- Shared records: `MediaSearchPlan<T>` (query intent, judge, wanted-since), `ReleaseJudgement<T>`, `ReleaseEvaluation<T>`, `SearchEvaluation<T>`, `GrabTarget`.
- Underneath: `IndexerSearchCoordinator`/`SearchPlanner` (Usenet search), `ReleaseSelectionEngine` + `QualityProfile`, `ReleaseRequestTracker` (tried releases, back-off), SABnzbd via the download-client layer, `CompletedDownloadDispatcher` for import.

Flow
WantedItems / request -> media adapter builds `MediaSearchPlan` -> `AcquisitionCore.SearchAsync` -> ranked `ReleaseEvaluation` list
-> `AcquisitionCore.GrabAsync(GrabTarget)` -> operation -> completed-download dispatcher -> media import adapter.

Media adapters on the core
- Video (Movie, Tv): `VideoAcquisitionEngine` (`VideoReleaseJudge`, unit and season-pack coverage, playback priority).
- Music: `MusicAcquisitionEngine` (`MusicReleaseJudge`).
- Manga and Light Novel: `ReadingAcquisitionEngine` (`ReadingReleaseJudge.Plan/Judge`, `DisplayScore` for the pages).

Not on the core yet (own orchestration still to be moved): Anime (`AnimeAcquisitionPipeline`), Book (`BookAcquisitionExecutor`, `BookUsenetSearch`), Audiobook.

Manual Search reads the same `SearchEvaluation` through `VideoManualSearchService`, `MusicManualSearchService`, `ReadingManualSearchService`.

Tests: `VideoAcquisitionRequestExecutorTests`, `MusicAcquisitionTests`, `VideoManualSearchTests`, `ReadingAcquisitionTests`, `ReleaseSelectionEngineTests`.
