# Jularr canonical domain model

Status: planning baseline. This document defines the target domain before further feature implementation. Existing legacy tables are not automatically part of the target model.

## 1. Principles

- One canonical media model for Anime, TV, Movie, Manga, Light Novel, Book, Audiobook and Music.
- Media-type-specific data exists only where the concept is genuinely different.
- Provider metadata never defines identity by itself.
- Physical files are separate from logical works, editions and releases.
- User state is separate from shared library state.
- Language, edition and version are explicit concepts.
- Acquisition, playback/reading, learning and AI consume the media core; they do not create competing media models.
- PostgreSQL is the canonical database.
- Legacy per-type models are migration sources, not permanent parallel sources of truth.

## 2. Canonical hierarchy

```text
Work
├─ Titles / External identities / Relations / Metadata provenance
├─ Structure
│  ├─ Season -> Episode        (video episodic)
│  └─ Volume -> Chapter        (written/serialized)
├─ Edition
│  └─ Version
│     └─ Asset
│        └─ File
│           └─ Track
└─ Artwork
```

Not every Work uses every level. A Movie can have no structural children. A TV/Anime Work uses seasons/episodes. Manga/Book/Light Novel use volumes/chapters. Audiobooks may map their playable chapters to the written work where identity is known.

## 3. Work

`Work` is the provider-independent intellectual/media work.

Core fields:
- `Id`
- `MediaType`: Anime, TvSeries, Movie, Manga, LightNovel, Book, Audiobook, Music (an album)
- `CanonicalTitle`
- lifecycle/status fields only when universally meaningful
- timestamps

Related entities:
- `WorkTitle`: localized, native, romanized, alias and alternate titles
- `WorkExternalIdentity`: AniList, TMDB, TVDB, IMDb, ISBN/OpenLibrary etc.
- `WorkRelation`: sequel, prequel, adaptation, source, spin-off, side story, alternative version etc.
- `WorkFieldProvenance`: source/provider for resolved metadata fields
- `Artwork`: poster, cover, banner, backdrop, logo and generated artwork

Provider IDs must be unique within `(Provider, MediaType, ExternalId)` and must be correctable without replacing the Work.

## 4. Structure

### Video episodic

`Season`
- belongs to Work
- season number/order
- optional title and metadata

`Episode`
- belongs to Work
- optionally Season
- season/episode and absolute numbering
- title, air/release information
- runtime where known

### Written/serialized

`Volume`
- belongs to Work
- number/order
- title

`Chapter`
- belongs to Work
- optionally Volume
- number/order
- title

Structure represents logical content. It must not depend on whether a local file exists.

### Music (manager MVP)

Music is a first-class media type for acquisition management (Lidarr replacement). It reuses the canonical model and adds no music-specific scheduler, scorer, downloader monitor, Operation store or retry engine.

- **Album = Work** (`MediaType = Music`). `CanonicalTitle` is the album title, `Year` its release year. Its provider identity is a `WorkExternalIdentity` with provider `musicbrainz` (the release group id).
- **Artist** is a small owning record (`MusicArtist`): name, sort name, MusicBrainz artist id, monitoring mode. An album belongs to exactly one primary artist through its `MusicAlbum` detail row (album type, release date, monitored). Artists are not Works; they group albums the way a Series groups episodes.
- **Track = structure** (`WorkTrack`: disc, number, title, duration), the music equivalent of an episode or chapter. It is logical content and exists without a file. It is not the technical `Track` (stream) below an Asset.
- **File side**: one `WorkVersion` per imported audio file, one `MediaAsset` (`Kind = Audio`, `WorkTrackId`) and one `StoredFile` per track file. An album is available when its tracks have files and partial while only some do.
- **Monitoring**: artist `All` / `Future` / `None` plus an album-level switch. A monitored album without files is Wanted through the shared `AcquisitionRequest` lifecycle (Kind `Music`, provider `musicbrainz`, external id = release group id). Future albums appear through the periodic artist refresh of the shared Wanted pass.
- **Quality**: the shared profile engine with document-style quality keys `FLAC`, `MP3-320`, `MP3-V0`, `MP3-256`, `MP3`; the default profile prefers FLAC and accepts lossy releases as temporary fallbacks.
- **Search**: the shared Search Planner, the Newznab `music` function (artist/album) where advertised, text fallbacks, categories 3000/3010/3040. Identity (artist words, album words, no different release type such as live/remix/karaoke, discography packs need review) is decided before any profile rule.
- **Import**: shared completed-download dispatcher; the destination is the default **Music** LibraryRoot (`LibraryContentType.Music`) resolved by Storage; files are matched to `WorkTrack` by disc/number or title and placed as `Artist/Album (Year)/NN - Title.ext`. Importing the same download twice never duplicates files.
- **Metadata provider**: MusicBrainz (read-only, no key) behind `IMusicMetadataProvider`; one request per second and an identifying User-Agent.
- **Out of scope**: streaming player, lyrics, scrobbling, recommendations, analysis, transcoding, multi-release (edition) management beyond what import correctness needs.

## 5. Edition

`Edition` represents a materially distinct publication/presentation of a Work.

Examples:
- Japanese original Light Novel
- official German translation
- English manga edition
- Blu-ray edition
- audiobook edition

Important fields:
- Work
- language
- edition/publication key
- format
- publisher
- ISBN where applicable
- title
- official/generated flag
- primary/preferred marker

A machine-translated book is never allowed to silently replace an official edition. It is a derived edition with provenance.

## 6. Version / release representation

`Version` represents a concrete content/release variant inside an Edition or Work.

Examples:
- WEB-DL 1080p release group A
- BluRay remux
- EPUB retail release
- generated German translation v2
- revised audiobook encode

Fields can include:
- Edition
- logical unit target (whole work, episode, chapter, volume etc.)
- quality
- release group/source
- language characteristics
- provenance
- generated/official state
- technical/release notes

This is distinct from an acquisition search result. A release candidate becomes a Version only when accepted/imported into canonical library state.

## 7. Assets, files and tracks

The current anime-specific `MediaFile` concept must evolve into media-independent storage entities.

`Asset`
- logical playable/readable artifact belonging to a Version
- target unit: Work/Episode/Volume/Chapter
- asset kind: Video, Audio, Ebook, ComicArchive, Subtitle, Image etc.

`File`
- physical stored file
- Asset
- LibraryRoot/storage location
- relative/path identity
- size, timestamps, fingerprint/hash where useful
- availability state

`Track`
- belongs to an applicable File
- kind: Video, Audio, Subtitle
- stream index
- codec/format
- language
- title
- channels/layout
- forced/default/commentary/sign metadata

Technical analysis belongs to File/Track and must not be tied to Anime/Episode classes.

## 8. Languages and translations

Use normalized language identifiers consistently rather than arbitrary feature-specific strings.

Translations are explicit derivations:

`Translation`
- source Edition/Version
- target language
- target derived Edition/Version
- provider/engine
- model/version when applicable
- generation timestamp
- quality/review state
- official vs machine-generated is always distinguishable

Acquisition policy:
1. search for an existing official/preferred edition in the requested language;
2. search configured legal metadata/acquisition sources and indexers;
3. when allowed and no suitable edition exists, generate a translated derivative;
4. cache/store the result as a versioned derivative rather than retranslating on every read.

Translation providers are pluggable: local/self-hosted engines can be default; optional external APIs can be configured. Reader can switch between available editions/languages.

## 9. Metadata and provider evidence

Metadata providers return candidates/evidence. They do not own canonical records.

Pipeline:

```text
Provider result -> persist provider evidence/snapshot -> identity resolution -> canonical Work -> field resolution/provenance
```

External provider data fetched for durable Jularr features should remain locally usable after fetch rather than making normal UI rendering depend on repeated provider calls.

### Localized metadata policy

Work identity is language-independent. Localized metadata is stored as language-tagged variants/evidence on the same Work; language-neutral facts are not duplicated per locale.

The canonical instance policy is defined by #820:
- Fixed mode: one admin-selected language for UI and required media metadata; personal language overrides are disabled.
- Free mode: profile languages are allowed; Admin may require the Library to retain metadata for every effective profile language.

When a new required locale appears, missing durable `(Work, locale)` metadata is filled through a deduplicated, provider-rate-aware background spool. Opening a Work promotes its missing locale fetch, while the page immediately renders the best locally persisted fallback.

Discover candidates remain bounded locale-aware provider cache/evidence and are not bulk-materialized in every instance language.

Durable artwork is likewise canonical/local-first: generic Work artwork slots may carry a locale for language-specific posters/logos, use neutral fallback, and maintain local Jularr-owned derivatives for normal Library rendering.

Conceptual `ProviderEntitySnapshot`:
- Provider;
- EntityKind;
- ExternalId;
- normalized searchable fields;
- provider payload snapshot where allowed/useful;
- fetched/refreshed timestamps;
- stale/refresh metadata;
- optional resolved WorkId.

Snapshots/evidence may become stale, but stale data remains locally readable until an explicit retention/cleanup policy removes it.

Metadata conflicts must be correctable. Provider evidence never replaces canonical Work identity.

## 10. Acquisition

Acquisition is media-independent.

Core concepts:
- `WantedItem`: desired canonical acquisition target/language/quality; for normal media this is Work/unit/Edition. Games remains a separate Games-owned identity and enters shared Acquisition through a typed target/application adapter rather than being modeled as a Work.
- `ReleaseCandidate`: transient/indexer result
- `DownloadJob`: accepted candidate handed to a download client
- `ImportJob`: downloaded material awaiting identification/import
- `AcquisitionEvent`: append-only history/audit
- `AcquisitionProfile`: reusable quality/language/release rules

Pipeline:

```text
Wanted
 -> SearchIntent / QueryPlan
 -> normalized + deduplicated ReleaseCandidate
 -> identity/safety/profile selection
 -> Grab
 -> Download
 -> Import
 -> Version/Asset/File
 -> Library
```

Binding acquisition planning semantics:
- search/query planning: `docs/ACQUISITION_SEARCH_PLANNER.md`;
- automatic release eligibility/ranking/fallback/upgrade: `docs/AUTOMATIC_RELEASE_SELECTION.md`.

Anime, TV, Movies, Books, Manga and Light Novels must not each implement a separate acquisition engine.

## 11. Accounts, Profiles and user state

Authentication/account state, personal Profile state and shared media data must remain separate.

Canonical concepts:
- `Account` — Jularr-owned authentication/security identity
- `AccountLoginIdentity` — linked external/local login identity for an Account
- `Profile` — personal media-state/preferences context owned/available to an Account
- `ProfileConnection` — optional external service connection used for profile sync/import/write-back
- `MediaProgress`
- `PlaybackHistory`
- `Bookmark`
- `Highlight`
- `WatchlistEntry`
- `UserRating`
- `UserMediaPreference`
- `PlaybackPreference`
- `ReaderPreference`

### Account vs Profile

Jularr always owns the internal Account identity.

An Account may authenticate through:
- local credentials;
- passkeys/security keys;
- one or more configured external login providers.

External identities such as Plex/Jellyfin/Trakt/AniList/MAL/Google are linked identities/evidence and never become the canonical Account ID.

One Account may own one or more Profiles when instance policy allows it. Login establishes the Account session; Profile selection chooses the active personal context.

Profile-scoped state includes progress, history, ratings, personal lists/collections where applicable, playback/reader preferences, Learning state and personal service Connections.

Account-scoped state includes authentication methods, security/recovery, account roles/capabilities and account-level sessions.

A provider may expose both Login and Connection/synchronization capabilities, but these remain separate relationships. Authenticating through a provider must not silently enable all sync/write-back capabilities.

Profile switching never merges or copies personal state implicitly.

A Profile PIN is an optional selection guard only. It is not an Account authentication factor or replacement for server-side Account authorization.

### Universal user ratings

`UserRating` is profile-scoped and references canonical media identity rather than media-type-specific tables.

Baseline semantics:

- Profile
- canonical Work target
- optional structural target only if unit-level ratings are explicitly supported later
- canonical normalized score
- created/updated timestamps
- optional review/comment reference when reviews are introduced

The stored score uses one universal normalized numeric domain independent of UI presentation. A recommended persistence representation is a constrained fixed-precision decimal in the inclusive range `0.0000..1.0000` (or an equivalent lossless normalized integer representation).

`NULL` / no row means **not rated**. It is not equivalent to the minimum score.

Per-profile `RatingDisplayPreference` controls only input and presentation. Supported systems include:

- three-level thumbs: Down / Up / Double Up
- 5 stars
- 0–10 integer
- 0.0–10.0 decimal
- 0–100

Changing the display system never migrates or rewrites stored ratings.

Continuous systems convert deterministically to/from the normalized score. Discrete systems bucket existing normalized scores for display; when the user actively selects a discrete value, that input maps to a defined canonical anchor.

This allows the same rating to appear consistently on Profile/Ratings, media detail pages, search/library cards where appropriate, and provider-sync adapters without creating competing rating models.

External-provider ratings are synchronization/presentation concerns. Provider score formats map to/from `UserRating`; they never become Jularr's canonical persistence scale.

`MediaProgress` targets a canonical Work or structural unit and records an appropriate position/state:
- video/audio: time position, completed/watched state
- book/manga: locator/page/chapter position, completed/read state

Avoid separate `EpisodeProgress`, `NovelProgress`, `AudiobookProgress` as permanent unrelated models when the state semantics can be represented canonically. Media-specific extension data is acceptable when actually required.

## 12. Playback and reading sessions

Playback is a service over canonical media assets.

`PlaybackPlan`
- selected Version/Asset/File
- Direct Play / Remux / Transcode decision
- selected audio/subtitle tracks
- reason/diagnostics

`ActiveSession`
- profile/device
- canonical media target
- current position/state
- selected tracks
- client capabilities
- timestamps

Reader sessions use the same canonical identity/progress principles but reader-specific presentation state stays in Reader preferences/session state.

Platform UX (Web desktop, mobile/PWA, tablet, TV, iOS/native) must share this session contract without sharing inappropriate control layouts.

## 13. Subtitles

Subtitle data belongs to the media asset/unit rather than Anime-specific Episode ownership.

Concepts:
- subtitle Track/file
- parsed `SubtitleCue` where Jularr needs interactive text
- language/profile selection rules
- generated/downloaded/embedded provenance

Learning subtitles are a presentation/learning layer over subtitle cues, not a second playback identity model.

## 14. Learning

Learning references canonical media identities but remains its own domain.

Two sources of learning content:
- media-derived learning (subtitle sentences, vocabulary, reading passages)
- media-independent curriculum/course learning

Keep:
- curriculum blueprint hierarchy
- shared course instances
- learner-specific course/progress state
- cards/reviews/FSRS-like scheduling
- learning context links to Work/Episode/Chapter where relevant

Learning must never require duplicating Work/Episode/Chapter records.

## 15. AI

AI is infrastructure/capability, not canonical media identity.

AI tasks may produce:
- explanations
- translations
- chapter artwork
- learning material
- metadata assistance

Every persistent AI-generated artifact records:
- task/type
- source identity/version
- provider/model when available
- generation version/settings needed for reproducibility
- visibility/ownership (shared instance result vs personal result)
- generated status/provenance

Server AI availability and personal user AI configuration are policy/configuration concerns layered over the same task contracts.

## 16. Collections and discovery

Collections are profile-scoped views/sets of canonical `Work` IDs across media types.

Persisted Collection modes:
- Manual — explicit membership/order;
- Smart — rule-driven membership over canonical/local facts and profile state;
- Linked — local membership synchronized from an external provider list.

Cross-media is a normal Collection capability, not a separate kind. Franchise/adaptation grouping uses canonical Work relations through Smart/derived views rather than a competing identity model.

A `CollectionEntry` references a local Work ID. It never stores provider IDs as canonical media identity or duplicates canonical title/progress/rating metadata as a source of truth.

Games does not enter this Work-based Collection model in V1. Games owns `Game` identity outside MediaCore; future cross-domain Collections require an explicit typed target contract rather than coercing Game into Work.

Linked Collection sync follows:

```text
External list
 -> persist provider list/item snapshots
 -> resolve/create canonical Work identities
 -> update local Collection membership by WorkId
 -> render from local Jularr state
```

A provider outage must not make an already-synced Linked Collection unreadable.

Ambiguous provider identity is retained as provider evidence/mapping work; Jularr must not silently merge Works from title similarity alone.

Discovery results for normal media are provider candidates until resolved to/associated with a Work. A user can discover media not yet locally available without creating a second library model.

Games is an explicit domain exception: Games discovery resolves to the Games module's canonical Game identity, not Work. Shared Search/Request presentation may carry a typed canonical target so this does not create a second acquisition/search stack.

Recommendations should return canonical/resolvable target references owned by the relevant domain.

## 17. Storage

`LibraryRoot` remains a storage concept, not a media type.

Files reference storage roots. NAS wake/retry/availability belongs to storage infrastructure. Logical Works and metadata remain available even while a storage root is offline.

Storage lifecycle changes physical availability/location; it does **not** redefine canonical Work identity.

Canonical rules:
- moving/tiering a File between compatible LibraryRoots keeps the same logical Work/Version/Asset identity where the stored-file contract permits it;
- deleting local bytes does not delete Work metadata, provider identity, progress/history or list state by default;
- a Work may remain in the Library as not locally available and later be reacquired through the normal acquisition pipeline;
- low free space is input to policy evaluation, never implicit permission to delete;
- active hard Requirements constrain version/track pruning, downgrade and optimization;
- policy/scoring/reacquisition-risk state is operational lifecycle state, not media identity;
- orphan file, missing reference, corrupt file and offline storage are separate states;
- physical duplicate consolidation must not silently merge canonical Works/editions;
- logical file size and physically reclaimable bytes may differ because of hardlinks/reflinks/dedupe/snapshots.

Ownership:
- #411 owns canonical storage availability and integrity-safety gates;
- #815 owns LibraryRoot routing/default future import placement;
- #414 owns lifecycle/retention/optimization/tiering/physical migration policy and audit;
- external-stack migration remains separate (#433).

Destructive lifecycle operations are explicit/audited and preserve user state unless a separate canonical domain operation intentionally changes it.

## 18. Identity and deletion rules

- Stable internal IDs are never derived from filenames or provider IDs.
- Moving/renaming a file does not create a new Work.
- Replacing/upgrading a release does not create a new Work.
- Changing metadata provider does not create a new Work.
- Merge/split/reassign operations are audited.
- User progress should survive file replacement and release upgrades because it targets canonical logical identity.

## 19. Current model transition

Known current parallel models include legacy/per-type entities such as:
- `Anime`, `Episode`, `MediaFile`
- `NovelWork`, `NovelVolume`, `NovelChapter`, `NovelTranslation`
- `Movie`, `TvSeries`
- `Audiobook`, `AudiobookFile`
- per-type progress models

The existing `Work*` model is the starting point for the canonical core, but it is not automatically considered complete. In particular, Assets/Files/Tracks, unified progress, acquisition targets and translation derivation need to be modeled explicitly before legacy removal.

`WorkSourceLink` is a migration bridge. It must not become a permanent excuse to maintain two sources of truth.

## 20. Migration rule

No destructive migration starts until each existing entity is classified:

- **KEEP**: already matches canonical model
- **EVOLVE**: canonical concept exists but schema/API must change
- **MIGRATE**: data moves into canonical model
- **DELETE AFTER MIGRATION**: legacy duplicate
- **FEATURE-SPECIFIC**: valid separate bounded-domain data

Migration must preserve user progress, metadata identity, local file associations, language/track information and acquisition history wherever meaningful.

## 21. Required follow-up documents

Before major feature coding resumes:

1. `DOMAIN-AUDIT.md`: map every current entity/table to the target model and classify KEEP/EVOLVE/MIGRATE/DELETE/FEATURE-SPECIFIC.
2. `ARCHITECTURE.md`: module boundaries, contracts, background jobs, provider interfaces, API ownership and dependency rules.
3. `UX.md`: complete information architecture and platform-specific UI behavior.
4. approved mockups for major user/admin flows.
5. implementation roadmap ordered by dependencies.

Until those are accepted, new feature work should not introduce new parallel media, progress, acquisition or provider models.
