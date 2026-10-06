# Jularr domain audit

Status: planning. Companion to `DOMAIN.md`. No destructive migration is authorized by this document alone.

Classification:
- **KEEP** — concept already belongs in the canonical model.
- **EVOLVE** — correct concept, but schema/ownership must change.
- **MIGRATE** — data must move into another canonical entity.
- **DELETE AFTER MIGRATION** — parallel legacy source of truth.
- **FEATURE-SPECIFIC** — valid bounded-domain data that should remain outside Media Core.

## 1. Main finding

Jularr currently has two overlapping media architectures:

1. legacy/per-type models (`Anime`, `Episode`, `NovelWork`, `Movie`, `TvSeries`, `Audiobook`, per-type files/progress etc.);
2. the newer universal `Work*` Media Core.

The target is one canonical Media Core. `WorkSourceLink` and `LegacyWorkBridge` are transitional mechanisms, not permanent architecture.

## 2. Canonical Media Core

| Current entity | Classification | Target |
|---|---|---|
| `Work` | KEEP/EVOLVE | Canonical root. Expand only with universally meaningful fields. |
| `WorkTitle` | KEEP | Canonical localized/alternate titles. |
| `WorkExternalIdentity` | KEEP | Canonical provider identity/evidence. |
| `WorkRelation` | KEEP | Cross-work relations/adaptations. |
| `WorkSeason` | KEEP/EVOLVE | Rename conceptually to canonical Season if useful; remains Work structure. |
| `WorkEpisode` | KEEP/EVOLVE | Canonical Episode. Must become the only episode identity. |
| `WorkVolume` | KEEP/EVOLVE | Canonical Volume. |
| `WorkChapter` | KEEP/EVOLVE | Canonical Chapter. |
| `WorkEdition` | KEEP/EVOLVE | Canonical Edition; add official/generated/provenance semantics. |
| `WorkVersion` | EVOLVE | Canonical imported content/release variant; clarify relationship to Asset/File. |
| `WorkFieldProvenance` | KEEP | Canonical field provenance. |
| `WorkIdentityChange` | KEEP | Append-only identity merge/split/reassign audit. |
| `WorkSourceLink` | DELETE AFTER MIGRATION | Temporary legacy bridge only. |

The existing MediaCore services (`WorkService`, `WorkQueryService`, `WorkStructureService`, duplicate detection, metadata field sources) should be audited and adapted around this model rather than replaced by another parallel abstraction.

## 3. Missing canonical storage layer

The current Media Core stops too early at `WorkVersion`. Introduce/standardize:

- `MediaAsset` — logical playable/readable artifact for Work/Episode/Volume/Chapter.
- `StoredFile` — physical file in a `LibraryRoot`.
- `MediaTrack` — embedded video/audio/subtitle stream.
- `MediaTechnicalAnalysis` — analysis of StoredFile/MediaTrack.

Names are provisional; architecture planning should choose final names and avoid collisions with current `MediaFile`.

## 4. Anime/video legacy

| Current entity | Classification | Target |
|---|---|---|
| `Anime` | MIGRATE + DELETE AFTER MIGRATION | `Work(MediaType=Anime)` |
| `Episode` | MIGRATE + DELETE AFTER MIGRATION | canonical `WorkEpisode` |
| `AnimeMetadata` | MIGRATE + DELETE AFTER MIGRATION | Work fields/titles/external identities/provenance/artwork |
| `AnimeLocalMetadata` | MIGRATE + DELETE AFTER MIGRATION | external identities + provenance/import evidence |
| `MediaFile` | EVOLVE/MIGRATE | `MediaAsset` + `StoredFile`, linked to canonical unit |
| `MediaAnalysis` | EVOLVE/MIGRATE | media-independent technical analysis |
| `MediaAnalysisStream` | EVOLVE/MIGRATE | canonical `MediaTrack`/technical stream data |
| `EpisodeMediaSegment` | EVOLVE | segment targets canonical Episode; keep segment bounded domain |
| `EpisodeSegmentDetectionState` | EVOLVE | canonical Episode target; feature-specific processing state |

No new feature should add another dependency on legacy `Anime.Id`/`Episode.Id` unless required solely for the controlled migration period.

## 5. Subtitles

| Current entity | Classification | Target |
|---|---|---|
| `SubtitleTrack` | EVOLVE/MIGRATE | canonical subtitle MediaTrack/Asset; no legacy Episode ownership |
| `SubtitleCue` | KEEP/EVOLVE | parsed cues linked to canonical subtitle identity |
| `SubtitleLanguageProfile` | FEATURE-SPECIFIC | user/admin subtitle policy |
| `SubtitleLanguageProfileItem` | FEATURE-SPECIFIC | profile rule |
| `SubtitleProfileAssignment` | FEATURE-SPECIFIC | assignment to profile/scope |

Embedded and external subtitles should share language/provenance semantics while retaining the physical distinction needed for playback.

## 6. Novels/books/manga

| Current entity | Classification | Target |
|---|---|---|
| `NovelWork` | MIGRATE + DELETE AFTER MIGRATION | canonical Work with LightNovel/Book/Manga type |
| `NovelVolume` | MIGRATE + DELETE AFTER MIGRATION | canonical WorkVolume |
| `NovelChapter` | MIGRATE + DELETE AFTER MIGRATION | canonical WorkChapter |
| `BookEdition` | MIGRATE/EVOLVE | canonical WorkEdition |
| `BookFile` | MIGRATE | canonical MediaAsset + StoredFile |
| `NovelTranslation` | EVOLVE/MIGRATE | explicit canonical Translation derivation + generated Edition/Version |
| `NovelAnimeMapping` | MIGRATE/EVOLVE | WorkRelation plus optional structural mapping where chapter ranges matter |
| `NovelProgress` | MIGRATE + DELETE AFTER MIGRATION | unified MediaProgress |
| `NovelBookmark` | EVOLVE | unified Bookmark targeting canonical content locator |
| `NovelHighlight` | EVOLVE | unified Highlight targeting canonical content locator |
| `NovelBookmarkTombstone` | FEATURE-SPECIFIC/EVOLVE | offline sync/reconciliation mechanism, not Media Core |
| `ReaderPreference` | KEEP/EVOLVE | user-state domain; replace legacy NovelWork FK with canonical Work scope |

Novel import, EPUB parsing, source providers and chapter document/text services remain useful capabilities but must emit/read canonical identities after migration.

## 7. Movies and TV

| Current entity | Classification | Target |
|---|---|---|
| `Movie` | MIGRATE + DELETE AFTER MIGRATION | `Work(MediaType=Movie)` |
| `TvSeries` | MIGRATE + DELETE AFTER MIGRATION | `Work(MediaType=TvSeries)` |

TMDB/TVDB/IMDb IDs move to `WorkExternalIdentity`; titles and paths must not remain duplicate canonical fields. Physical paths belong to storage/files, not Work.

## 8. Audiobooks

| Current entity | Classification | Target |
|---|---|---|
| `Audiobook` | MIGRATE + DELETE AFTER MIGRATION | Work/Edition with audiobook semantics |
| `AudiobookFile` | MIGRATE + DELETE AFTER MIGRATION | MediaAsset + StoredFile |
| `AudiobookProgress` | MIGRATE + DELETE AFTER MIGRATION | unified MediaProgress |

Narrator and audiobook-specific publication metadata should survive as edition/media-specific metadata rather than justify a parallel library model.

## 9. Progress and user state

| Current entity | Classification | Target |
|---|---|---|
| `EpisodeProgress` | MIGRATE + DELETE AFTER MIGRATION | unified MediaProgress targeting canonical Episode |
| `EpisodePlaybackHistoryEntry` | MIGRATE/EVOLVE | unified PlaybackHistory |
| `NovelProgress` | MIGRATE + DELETE AFTER MIGRATION | unified MediaProgress |
| `AudiobookProgress` | MIGRATE + DELETE AFTER MIGRATION | unified MediaProgress |
| `ProfilePlaybackPreferences` | KEEP/EVOLVE | canonical per-profile playback preferences |
| `ReaderPreference` | KEEP/EVOLVE | reader-specific presentation preferences |
| `OfflineProgressReconciliation` logic | FEATURE-SPECIFIC/EVOLVE | generic reconciliation over canonical progress IDs |
| `EpisodeSequence` logic | EVOLVE | derive ordering from canonical WorkEpisode structure |

Unified progress must support time-based and document-locator positions without creating a single giant nullable table. Architecture should define a common identity/state envelope with typed position data or narrowly scoped extensions.

## 10. Acquisition

| Current entity | Classification | Target |
|---|---|---|
| `AcquisitionHistoryEntry` | EVOLVE/MIGRATE | generic AcquisitionEvent; remove Anime-only ownership |
| `AcquisitionApiKey` | KEEP | acquisition API/auth infrastructure |

The broader acquisition implementation must converge on canonical `WantedItem`, `ReleaseCandidate`, `DownloadJob`, `ImportJob`, `AcquisitionEvent`, and reusable profiles. Existing anime-specific fields (`AnimeId`, season, episode) must become canonical target references.

## 11. Collections/watchlist/discovery

| Current entity | Classification | Target |
|---|---|---|
| `Collection` | KEEP | cross-media collection owned by profile |
| `CollectionItem` | KEEP | already references Work |
| Watchlist entities/services | EVOLVE | canonical Work references only |
| Discovery provider results | FEATURE-SPECIFIC | transient provider candidates resolved to Work when persisted |

No collection/discovery subsystem should persist parallel Anime/Movie/Novel identity once canonical Work resolution exists.

## 12. Learning

### Keep as bounded domain

- `Term`
- `LearningUnit`
- `LearningVariant`
- `LearningCourse`
- `LearningCard`
- `LearningCardReview`
- `LearningContext`
- `LearningPreferences`
- `CurriculumBlueprint`
- `CurriculumLevel`
- `CurriculumChapter`
- `CurriculumLesson`
- `CurriculumExercise`
- `SharedCourseInstance`
- `LearnerCourse`
- `LearnerCourseItemDelta`
- `LearnerCourseProgress`

Classification: **FEATURE-SPECIFIC / KEEP**, subject to a later learning-domain audit.

`EpisodeTerm` and any media-learning link must **EVOLVE** to reference canonical Work/structural units instead of legacy Episode identity.

Learning remains separate because curriculum/cards/reviews are not media-library entities. Only context links cross the boundary.

## 13. AI

`AiSentenceExplanationCache` and other AI caches/artifacts are **FEATURE-SPECIFIC/EVOLVE**.

They must reference canonical source identities where persistent, and generated artifacts must carry provider/model/generation/provenance information when required by `DOMAIN.md`.

AI configuration must not become a second ownership model for media or translations.

## 14. Auth/profile

`OwnerAccount` is **KEEP/EVOLVE** pending the architecture/auth audit. Authentication/account entities are not Media Core. Profile identity should become explicit and consistent because many current entities use free-form `ProfileId` strings.

A dedicated Profile model should be evaluated before unified progress/preferences are implemented.

## 15. Storage

`LibraryRoot` is **KEEP/EVOLVE**.

Keep NAS/storage availability, Wake-on-LAN and path-root concerns in storage infrastructure. Canonical media identity must remain usable when a root is offline. Stored files reference roots; Works do not own library paths.

## 16. Legacy bridge

`LegacyWorkBridge` and `WorkSourceLink` are **MIGRATION-ONLY**.

Required end state:
- every surviving library item has a canonical Work identity;
- structural children have canonical IDs;
- files/assets target canonical IDs;
- progress and history target canonical IDs;
- acquisition targets canonical IDs;
- UI/query services no longer require per-type legacy IDs;
- bridge tables/services can then be deleted.

Do not expand the bridge to support new features.

## 17. AppDbContext

Current `AppDbContext` owns mappings for many unrelated domains and contains both canonical and legacy models.

Classification: **EVOLVE**.

Architecture planning should split EF configuration by bounded domain/entity configuration without necessarily creating many physical DbContexts. A single PostgreSQL transaction boundary can remain where useful; the goal is ownership/readability, not fragmentation for its own sake.

Do not create dozens of tiny abstractions merely to reduce file length.

## 18. Migration order

Recommended dependency order:

1. Freeze new legacy-model dependencies.
2. Finalize canonical Profile identity.
3. Finalize Work/Structure/Edition/Version semantics.
4. Add canonical Asset/File/Track layer.
5. Backfill Works and structural units from Anime/Novel/Movie/TV/Audiobook records.
6. Migrate physical files and technical analysis.
7. Migrate subtitles/segments to canonical units/files.
8. Add unified progress/history and migrate per-type user state.
9. Convert acquisition to canonical targets.
10. Convert reader/player/library/discovery/watchlist queries to canonical IDs.
11. Verify counts, identities, file links and progress with migration tests.
12. Remove per-type duplicate tables/services and then remove `WorkSourceLink`/`LegacyWorkBridge`.

No big-bang destructive migration. Each stage must be reversible until validation passes.

## 19. Data that must survive migration

Mandatory preservation:
- Work/media identity and provider IDs
- titles and localized titles
- season/episode and volume/chapter ordering
- editions/languages/ISBNs
- files, paths, fingerprints and technical analysis
- audio/subtitle language and stream information
- progress/history/bookmarks/highlights
- metadata provenance
- acquisition history where meaningful
- collections/watchlists
- media-to-learning context links
- generated translation provenance

## 20. Architecture decisions and remaining migration work

The architecture/UX acceptance gate that originally blocked broad feature implementation has been passed. The questions formerly listed here are no longer all open.

Current decision state:

1. **Asset/File/Track names — settled.**  
   Canonical storage uses `MediaAsset`, `StoredFile`, `MediaTrack` and `MediaTechnicalAnalysis`.

2. **Version / playable-target ownership — settled for the first video vertical, still needs broader migration completion.**  
   Movie playback targets Work; Anime/TV playback targets WorkEpisode. WorkVersion carries release/version provenance and MediaAsset owns the canonical playable/readable artifact relationship. Reading/Audiobook migrations must follow the same canonical hierarchy without inventing polymorphic legacy shortcuts.

3. **Unified progress — settled conceptually and implemented for canonical video; Reader migration remains open.**  
   `MediaProgress` is the canonical consumption-state envelope. Exact resume and completion are separate semantics. Timed video progress is implemented; Reader must complete stable `ReaderLocator`/document-position migration rather than add a second progress store.

4. **Account/Profile ownership — settled as a product/domain contract; legacy storage cleanup may remain.**  
   Account owns authentication/security/authorization. Profile owns personal progress/history/ratings/preferences/connections. Remaining free-form or legacy profile identifiers must migrate toward that contract rather than redefining ownership.

5. **Module boundaries — settled in `ARCHITECTURE.md`.**  
   MediaCore, Library, Metadata, Discovery, Acquisition, Playback, Progress, Reader, Learning, Accounts, Devices and Admin/Operations have explicit ownership/dependency rules.

6. **Provider contracts — settled architecturally, implementation remains incremental.**  
   Metadata, discovery, identity/login, profile connections, release search, subtitles and translation use capability-specific provider ports. Provider-native records remain evidence/mappings, never canonical media identity.

7. **Background jobs/idempotency — settled architecturally, implementation verification remains per vertical.**  
   Durable/retryable work must use explicit Job/Operation state, stable correlation and idempotent handlers. Acquisition/import/retry paths need final E2E validation, not another job architecture.

8. **Client/API contracts — settled as capability-oriented DTO/application contracts, still implemented slice by slice.**  
   Web/PWA/native clients consume canonical application contracts and must not depend on EF entities or raw filesystem identity.

9. **Legacy bridge removal — criteria settled.**  
   Remove a legacy read/write path only after canonical backfill, parity and validation of identity/files/progress/provider mappings. `WorkSourceLink` / `LegacyWorkBridge` remain migration-only until then.

10. **AppDbContext modularization — still implementation debt.**  
    EF configuration should continue moving toward bounded-domain ownership without forcing unnecessary physical DbContext fragmentation.

Current implementation-readiness details for the first video vertical are recorded in:
`docs/implementation/request-to-play-readiness-audit.md`.

## 21. Coding gate

Major implementation is no longer globally paused for lack of architecture/UX planning.

Current gate:

- follow `IMPLEMENTATION-ROADMAP.md` and binding screen specs;
- no new parallel media identities;
- no new per-media progress/session systems;
- no new media-specific acquisition scheduler when the shared pipeline owns the concern;
- no expansion of `LegacyWorkBridge` / `WorkSourceLink` into permanent product architecture;
- migrations/backfills must be reversible and validated before legacy deletion;
- legacy reads/writes may remain only while required for controlled compatibility;
- final vertical completion requires integration/E2E proof, not only foundation classes.
